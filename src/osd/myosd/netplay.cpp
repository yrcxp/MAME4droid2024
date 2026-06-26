// license:BSD-3-Clause
// copyright-holders: David Valdeita (Seleuco)
/***************************************************************************

    netplay.cpp

   MAME4droid Lock-Step Netplay Architecture

***************************************************************************/

/* ==========================================================================
 * MAME4droid Lock-Step Netplay Architecture
 * ==========================================================================
 * 
 * OVERVIEW:
 * This is a custom UDP-based Lock-Step Netplay implementation.
 * MAME emulation is entirely deterministic. For two clients to remain 
 * synchronized, they must execute the exact same inputs on the exact same frames.
 * 
 * To achieve this, the protocol enforces a strict "Lock-Step" execution:
 * The emulator cannot simulate frame N until it has received the peer's inputs 
 * for frame N. If the inputs have not arrived, the emulation thread blocks.
 * 
 * JITTER BUFFER & INPUT LAG (FRAMESKIP):
 * To hide network latency and prevent constant blocking, the system uses a 
 * predefined Input Lag, internally called `frame_skip` (or `target_delay`).
 * If the delay is 4 frames:
 *   - At frame 100, we sample the local joystick.
 *   - We send this input to the peer, tagged for execution at frame 104.
 *   - The emulator then executes frame 100 (using inputs sampled at frame 96).
 * As long as the network latency is lower than the time it takes to render 
 * 4 frames (~66ms), the inputs arrive ahead of time, and the game never stutters.
 * 
 * INTEGRATION WITH MAME:
 * 1. netplay_pre_frame_net(): 
 *    Called by the OSD layer just before MAME executes a video frame. 
 *    This function implements the Lock-Step blocking loop. It waits until 
 *    the peer's input for the current `target_frame` is available in the buffer.
 *    Once available, it injects both local and peer inputs into MAME's input ports.
 * 
 * 2. netplay_post_frame_net(): 
 *    Called immediately after MAME finishes executing the frame.
 *    It samples the new local inputs, prepares them for execution N frames 
 *    in the future, advances the frame counters, and transmits the UDP packet 
 *    to the peer. It also runs the Auto-Frameskip evaluation every 5 seconds.
 * 
 * RESILIENCE:
 * - N-1 History: Every UDP packet contains the inputs for both the current 
 *   frame and the previous frame. If a single packet is lost, the next packet 
 *   transparently recovers the lost data, preventing emulation stalls.
 * - Auto-Frameskip: Automatically adjusts the input lag (Jitter Buffer) based 
 *   on real-time network conditions, finding the optimal sweet spot between 
 *   smoothness and responsiveness.
 * ==========================================================================
 */

#include <stdio.h>
#include <string.h>
#include <errno.h>
#include <time.h>

#include "netplay.h"

#include <netinet/in.h>
#include <unistd.h>
#include <android/log.h>

#define NLOG(...) __android_log_print(ANDROID_LOG_DEBUG, "MAME4droid_Netplay", __VA_ARGS__)
#define NLOG_VERBOSE(...) do { if(0) __android_log_print(ANDROID_LOG_DEBUG, "MAME4droid_Netplay", __VA_ARGS__); } while(0)

// Netplay C++ helper functions imported from myosd-droid.cpp
extern void myosd_droid_netplay_set_exitPause(int val);
extern void myosd_droid_netplay_force_pause();
extern int myosd_droid_netplay_get_inMenu();
extern int myosd_droid_netplay_get_ext_status();
extern unsigned long myosd_netplay_joystick_read(int i);
extern float myosd_netplay_joystick_read_analog(int i, char axis);
extern unsigned long myosd_netplay_mouse_read(int i);
extern float myosd_netplay_mouse_read_analog(int i, char axis);
extern float myosd_netplay_lightgun_read_analog(int i, char axis);

static netplay_t netplay_player;

/* Byte-order helpers
 * We use memcpy so the compiler never generates an unaligned load/store
 * even inside pack(1) structs.  All multi-byte fields in netplay_msg_t
 * live at odd offsets due to pack(1), so plain cast-and-ntohl is UB on
 * strict-alignment platforms.                                              */

void htonf_inplace(float* dest, float value) {
    uint32_t temp;
    memcpy(&temp, &value, 4);
    temp = htonl(temp);
    memcpy(dest, &temp, 4);
}

float ntohf_inplace(const float* src) {
    uint32_t temp;
    memcpy(&temp, src, 4);
    temp = ntohl(temp);
    float value;
    memcpy(&value, &temp, 4);
    return value;
}

/* Decode an entire netplay_state from a packed network message into a
 * host-order netplay_state_t.  Centralises the 13-field ntoh fan-out
 * that was duplicated three times in the original code.                    */
static void decode_peer_state(netplay_state_t *out, const netplay_state_t *in_net)
{
    out->digital      = ntohl(in_net->digital);
    out->analog_x     = ntohf_inplace(&in_net->analog_x);
    out->analog_y     = ntohf_inplace(&in_net->analog_y);
    out->analog_rx    = ntohf_inplace(&in_net->analog_rx);
    out->analog_ry    = ntohf_inplace(&in_net->analog_ry);
    out->analog_lz    = ntohf_inplace(&in_net->analog_lz);
    out->analog_rz    = ntohf_inplace(&in_net->analog_rz);
    out->mouse_status = ntohl(in_net->mouse_status);
    out->mouse_x      = ntohf_inplace(&in_net->mouse_x);
    out->mouse_y      = ntohf_inplace(&in_net->mouse_y);
    out->lightgun_x   = ntohf_inplace(&in_net->lightgun_x);
    out->lightgun_y   = ntohf_inplace(&in_net->lightgun_y);
    out->ext          = ntohs(in_net->ext);
}

/* Encode a host-order netplay_state_t into a packed network message.      */
static void encode_peer_state(netplay_state_t *out_net, const netplay_state_t *in)
{
    out_net->digital      = htonl(in->digital);
    htonf_inplace(&out_net->analog_x,   in->analog_x);
    htonf_inplace(&out_net->analog_y,   in->analog_y);
    htonf_inplace(&out_net->analog_rx,  in->analog_rx);
    htonf_inplace(&out_net->analog_ry,  in->analog_ry);
    htonf_inplace(&out_net->analog_lz,  in->analog_lz);
    htonf_inplace(&out_net->analog_rz,  in->analog_rz);
    out_net->mouse_status = htonl(in->mouse_status);
    htonf_inplace(&out_net->mouse_x,    in->mouse_x);
    htonf_inplace(&out_net->mouse_y,    in->mouse_y);
    htonf_inplace(&out_net->lightgun_x, in->lightgun_x);
    htonf_inplace(&out_net->lightgun_y, in->lightgun_y);
    out_net->ext          = htons(in->ext);
}

static uint32_t netplay_get_ticks_ms() {
    struct timeval tv;
    gettimeofday(&tv, NULL);
    return (uint32_t)((tv.tv_sec * 1000) + (tv.tv_usec / 1000));
}

netplay_t * netplay_get_handle(){
    static int init = 0;    
    if(!init)
    {
        netplay_init_handle(&netplay_player);
        init = 1;
    }
    return &netplay_player;
}

int netplay_init_handle(netplay_t *handle){
    char temp_game_name[MAX_GAME_NAME] = "";
    if (handle->game_name[0] != '\0') {
        strncpy(temp_game_name, handle->game_name, MAX_GAME_NAME - 1);
    }
    
    /* Preserve frame_skip across reset: it may have been set by the UI
     * before netplayInit was called. Default to 2 if never set.           */
    int saved_frame_skip = (handle->frame_skip > 0) ? handle->frame_skip : 2;
    int saved_auto = handle->is_auto_frameskip;

    /* Wake any game thread that may be sleeping on the cond before we
     * destroy the primitives.  Harmless if not yet initialised (zero-init
     * pthread_mutex_t / pthread_cond_t are valid on Linux / Android Bionic
     * and equivalent to PTHREAD_MUTEX_INITIALIZER / PTHREAD_COND_INITIALIZER). */
    pthread_mutex_lock(&handle->sync_mutex);
    pthread_cond_broadcast(&handle->sync_cond);
    pthread_mutex_unlock(&handle->sync_mutex);

    pthread_mutex_destroy(&handle->sync_mutex);
    pthread_cond_destroy(&handle->sync_cond);

    memset(handle, 0, sizeof(netplay_t));

    /* Re-initialise synchronisation primitives after the memset.          */
    pthread_mutex_init(&handle->sync_mutex, NULL);
    pthread_cond_init(&handle->sync_cond, NULL);

    handle->frame_skip      = saved_frame_skip;
    handle->is_auto_frameskip = saved_auto;
    handle->peer_frame_skip = saved_frame_skip;
    
    if (temp_game_name[0] != '\0') {
        strncpy(handle->game_name, temp_game_name, MAX_GAME_NAME - 1);
    }
    
    handle->has_connection = 0;
    
    time(&handle->basetime);
        
    return 1;
}

void netplay_warn_hangup(netplay_t *handle)
{
    char msg[] = "Netplay: Connection lost. Resuming offline play...";
    
    if(handle->netplay_warn!=0)
        handle->netplay_warn(msg);
    else
        printf("%s",msg);
}

void netplay_warn_disconnect(netplay_t *handle)
{
    char msg[] = "Netplay: Peer disconnected. Resuming offline play...";
    
    if(handle->netplay_warn!=0)
        handle->netplay_warn(msg);
    else
        printf("%s",msg);
}

/* IS_SYNCED 
 * Returns true when no blocking wait is needed:
 *   • We are on an intermediate frame (frame < target_frame): the peer's
 *     data is not yet required, so advance freely.
 *   • We are on a sync frame (frame == target_frame) AND we have received
 *     the peer's input for that frame.
 *
 * MUST be evaluated while holding handle->sync_mutex.                      */
#define IS_SYNCED(h) \
    ( (h)->frame < (h)->target_frame || \
      ( (h)->frame == (h)->target_frame    && \
        (h)->peer_frame == (h)->target_frame ) )

/* Maximum total wait (ms) before declaring hangup.                         */
#define SYNC_TIMEOUT_MS      30000
/* How often we re-transmit while waiting (ms).  Replaces old 1 ms spin.   */
#define RETRANSMIT_MS        24

/* netplay_read_data ─────────────────────────────────────────────────── */

int netplay_read_data(netplay_t *handle)
{
    netplay_msg_t msg;

    if(!handle->read_pkt_data(handle, &msg))
        return 0;

    uint32_t msg_packet_uid = ntohl(msg.packetid);

    /* Strict Packet Drop:
     * Dropping older packets is mathematically correct in N-1 lockstep.
     * If packet N arrives after N+1, N+1 already recovered N. Accepting N late
     * only generated useless processing and exacerbated ACK storms. */
    if (msg_packet_uid <= handle->recv_packet_uid) {
        return 1;
    }

    handle->recv_packet_uid = msg_packet_uid;
    handle->last_recv_time_ms = netplay_get_ticks_ms();

    msg.msg_type = ntohl(msg.msg_type);

    switch (msg.msg_type) {

    case NETPLAY_MSG_DATA:
    {
        if (!handle->has_begun_game)
            break;

        uint32_t msg_timestamp  = ntohl(msg.u.data.timestamp);
        uint32_t msg_echo       = ntohl(msg.u.data.echo_timestamp);
        uint32_t peer_frame     = ntohl(msg.u.data.peer_frame);
        uint32_t peer_frame_prev= ntohl(msg.u.data.peer_frame_prev);

        /* Lock: all shared peer-state writes go under sync_mutex ──── */
        pthread_mutex_lock(&handle->sync_mutex);

        /* RTT: echo_timestamp is the timestamp we embedded in a previous
         * packet and the peer is echoing back.  RTT = now − that stamp.  */
        if (msg_timestamp != 0)
            handle->last_peer_timestamp = msg_timestamp;

        if (msg_echo != 0) {
            uint32_t rtt = netplay_get_ticks_ms() - msg_echo;
            if (rtt < 2000) {   /* discard absurd spikes                  */
                uint32_t fast_rtt = (handle->fast_rtt == 0)
                    ? rtt
                    : (handle->fast_rtt * 3 + rtt) / 4; /* EMA fast α=1/4 */

                uint32_t slow_rtt = (handle->smoothed_rtt == 0)
                    ? rtt
                    : (handle->smoothed_rtt * 7 + rtt) / 8; /* EMA slow α=1/8 */

                handle->fast_rtt = fast_rtt;
                handle->smoothed_rtt = slow_rtt;

                if (handle->max_rtt_interval == 0 || rtt > handle->max_rtt_interval) {
                    handle->max_rtt_interval = rtt;
                } else {
                    /* Decaying Maximum: allows the Peak RTT to slowly track downwards 
                     * over several seconds, but jumps upwards instantly on jitter spikes. 
                     * This creates a stable envelope over the network's maximum latency. */
                    handle->max_rtt_interval = (handle->max_rtt_interval * 63 + rtt) / 64;
                }
                    
                if (handle->min_rtt_window == 0 || rtt < handle->min_rtt_window) {
                    handle->min_rtt_window = rtt;
                } else {
                    /* Decaying Minimum: allows the baseline to slowly track upwards 
                     * if the physical ping genuinely increases, but quickly grabs 
                     * the minimum to filter out jitter spikes. */
                    handle->min_rtt_window = (handle->min_rtt_window * 31 + rtt) / 32;
                }
            }
        }

        handle->is_peer_paused = msg.u.data.is_peer_paused;

        NLOG_VERBOSE("recv peer_frame=%u peer_peer_frame=%u",
                     peer_frame, ntohl(msg.u.data.peer_peer_frame));

        int signaled = 0;
        int state_updated = 0;

        /* Primary path: packet carries exactly the frame we are waiting for. */
        if (handle->target_frame == peer_frame) {
            if (handle->peer_frame != peer_frame) {
                decode_peer_state(&handle->peer_state_tmp, &msg.u.data.peer_state_tmp);
                handle->peer_frame = peer_frame;
                signaled = 1;
                state_updated = 1;
                NLOG_VERBOSE("primary: peer_frame=%u matches target_frame", peer_frame);
            }
        }

        /* N-1 recovery: current packet carries the previous frame
         * as redundancy.  If that previous frame is exactly what we need
         * (i.e. we lost the original packet for it), recover here.        */
        if (!signaled && peer_frame_prev != 0 &&
            handle->target_frame == peer_frame_prev) {
            
            if (handle->peer_frame != peer_frame_prev) {
                decode_peer_state(&handle->peer_state_tmp, &msg.u.data.peer_state_prev);
                handle->peer_frame = peer_frame_prev;
                signaled = 1;
                state_updated = 1;
                handle->recovery_n1_count++;
                NLOG("RECOVERY: recovered frame %u from peer N-1 history", peer_frame_prev);

                /* RECOVERY PREFETCH: When N-1 fires, peer_frame is always the
                 * next sync point in lockstep. Store it immediately,
                 * frame_skip-agnostic, to guarantee no second stutter even during
                 * epoch transitions where local/peer frame_skip may differ briefly. */
                if (peer_frame != 0 && handle->peer_next_frame != peer_frame) {
                    decode_peer_state(&handle->peer_next_state_tmp, &msg.u.data.peer_state_tmp);
                    handle->peer_next_frame = peer_frame;
                    state_updated = 1;
                    NLOG("RECOVERY+PREFETCH: also secured peer_frame=%u for next sync", peer_frame);
                }
            }
        }

        /* Pre-fetch: the peer is one sync-point ahead of what we need.
         * Store it so post_frame_net can promote it instantly.            */
        if (handle->target_frame == handle->peer_frame &&
            handle->target_frame + handle->frame_skip == peer_frame) {
            if (handle->peer_next_frame != peer_frame) {
                decode_peer_state(&handle->peer_next_state_tmp, &msg.u.data.peer_state_tmp);
                handle->peer_next_frame = peer_frame;
                state_updated = 1;
                NLOG_VERBOSE("pre-fetched next peer_frame=%u", peer_frame);
            }
        }

        /* peer_peer_frame: what the peer says it has received from us.   */
        uint32_t peer_peer_frame = ntohl(msg.u.data.peer_peer_frame);
        if (handle->peer_peer_frame < peer_peer_frame)
            handle->peer_peer_frame = peer_peer_frame;

        /* Epoch Handshake:
         * We unconditionally accept epoch frameskip updates as long as they
         * arrive via a strictly newer packet (enforced by recv_packet_uid).
         * This guarantees synchronized frame delays across both peers. */
        uint32_t peer_epoch_frame = ntohl(msg.u.data.frameskip_epoch_frame);
        uint8_t  peer_epoch_value = msg.u.data.frameskip_epoch_value;
        if (peer_epoch_frame > handle->last_epoch_received) {
            handle->last_epoch_received = peer_epoch_frame;
            uint32_t local_frame = handle->frame; /* benign read of game-thread field */
            if (peer_epoch_frame >= local_frame) {
                /* Normal: epoch is in the future – schedule it.          */
                handle->frameskip_epoch_frame = peer_epoch_frame;
                handle->frameskip_epoch_value = peer_epoch_value;
                NLOG("Epoch RECEIVED: frameskip=%u at frame=%u (now=%u)",
                     peer_epoch_value, peer_epoch_frame, local_frame);
            } else {
                /* Late arrival: epoch frame already passed.
                 * Apply at the next post_frame_net check by scheduling it
                 * for the current frame (>= check will fire immediately). */
                handle->frameskip_epoch_frame = local_frame;
                handle->frameskip_epoch_value = peer_epoch_value;
                NLOG("Epoch LATE (now=%u, was for %u): applying immediately",
                     local_frame, peer_epoch_frame);
            }
            state_updated = 1;
        }

        /* Signal the game thread if we have what it is waiting for.      */
        if (signaled)
            pthread_cond_signal(&handle->sync_cond);

        pthread_mutex_unlock(&handle->sync_mutex);
        /* ── End lock ─────────────────────────────────────────────────── */

        /* Conditional ACK:
         * We only send an ACK if this packet meaningfully updated our state.
         * This eliminates ACK amplification storms during burst transmission. */
        if (state_updated) {
            /* Send ACK outside the lock to avoid holding mutex during I/O. */
            netplay_send_data(handle);
        }
    }
    break;

    case NETPLAY_MSG_JOIN:
    {
        NLOG("received NETPLAY_MSG_JOIN: has_begun_game=%d", handle->has_begun_game);
        /* Always answer JOIN with JOIN_ACK.  UDP packets can be dropped,
         * and if the Server starts the game before the Client receives the
         * ACK, we must not ignore the Client's retries.                   */
        handle->has_joined = 1;
        NLOG("sending join_ack");
        if (!netplay_send_join_ack(handle)) {
            NLOG("failed to send join_ack");
            return 0;
        }
    }
    break;

    case NETPLAY_MSG_JOIN_ACK:
    {
        handle->has_joined  = 1;
        handle->frame_skip  = msg.u.join.frame_skip;
        handle->basetime    = ntohl(msg.u.join.time);
        strncpy(handle->game_name, msg.u.join.game_name, MAX_GAME_NAME - 1);
        handle->game_name[MAX_GAME_NAME - 1] = '\0';
        NLOG("received join ack for %s with basetime:%s..",
             handle->game_name, ctime(&handle->basetime));
    }
    break;

    case NETPLAY_MSG_DISCONNECT:
    {
        NLOG("received disconnect message from peer – terminating connection.");
        netplay_warn_disconnect(handle);
        handle->has_connection = 0;
        /* Wake the game thread if it is sleeping in the sync wait.       */
        pthread_mutex_lock(&handle->sync_mutex);
        pthread_cond_broadcast(&handle->sync_cond);
        pthread_mutex_unlock(&handle->sync_mutex);
    }
    break;

    default:
        printf("netplay unknown msg %d\n", msg.msg_type);
        break;
    }

    return 1;
}

/* ── netplay_send_data ─────────────────────────────────────────────────── */

int netplay_send_data(netplay_t *handle)
{
    netplay_msg_t msg;

    if (!handle->has_connection)
        return 0;

    /* FIX: use atomic increment consistently (was plain += in JOIN/DISC). */
    uint32_t uid = __sync_add_and_fetch(&handle->packet_uid, 1);
    msg.packetid  = htonl(uid);
    msg.msg_type  = htonl(NETPLAY_MSG_DATA);

    msg.u.data.is_peer_paused = myosd_is_paused();
    msg.u.data.peer_frame     = htonl(handle->target_frame);

    /* Build the local state snapshot. We must NOT strip or filter inputs 
     * (like START+SELECT or in-menu) because altering the input stream 
     * sent to the peer breaks lockstep determinism. */
    netplay_state_t to_send = handle->state_tmp;

    encode_peer_state(&msg.u.data.peer_state_tmp, &to_send);

    /* N-1 History implementation:
     * Every outbound packet carries the inputs for the PREVIOUS frame.
     * This allows the receiver to recover from a single dropped packet 
     * without halting the emulation. */
    msg.u.data.peer_frame_prev = htonl(handle->prev_target_frame);
    encode_peer_state(&msg.u.data.peer_state_prev, &handle->prev_state_sent);

    msg.u.data.timestamp      = htonl(netplay_get_ticks_ms());
    msg.u.data.echo_timestamp = htonl(handle->last_peer_timestamp);

    msg.u.data.peer_peer_frame = htonl(handle->peer_frame);
    msg.u.data.peer_frame_skip = handle->frame_skip;

    /* Snapshot epoch fields under the mutex to prevent a torn read.      */
    pthread_mutex_lock(&handle->sync_mutex);
    msg.u.data.frameskip_epoch_frame = htonl(handle->frameskip_epoch_frame);
    msg.u.data.frameskip_epoch_value = handle->frameskip_epoch_value;
    pthread_mutex_unlock(&handle->sync_mutex);
    int ret = handle->send_pkt_data(handle, &msg);

    /* Update N-1 history has been moved to netplay_post_frame_net
     * so that retransmissions don't overwrite the previous history.       */

    return ret;
}

/* ── JOIN / JOIN_ACK / DISCONNECT helpers ──────────────────────────────── */

int netplay_send_join(netplay_t *handle){
    netplay_msg_t msg;
    memset(&msg, 0, sizeof(msg));

    /* FIX: was plain +=, which is not atomic and races with send_data.   */
    uint32_t uid  = __sync_add_and_fetch(&handle->packet_uid, 1);
    msg.packetid  = htonl(uid);
    msg.msg_type  = htonl(NETPLAY_MSG_JOIN);
        
    NLOG("netplay_send_join calling send_pkt_data");
    int ret = handle->send_pkt_data(handle, &msg);
    NLOG("netplay_send_join send_pkt_data returned %d", ret);
    return ret;
}

int netplay_send_join_ack(netplay_t *handle){
    netplay_msg_t msg;
    memset(&msg, 0, sizeof(msg));

    uint32_t uid  = __sync_add_and_fetch(&handle->packet_uid, 1);
    msg.packetid  = htonl(uid);
    msg.msg_type  = htonl(NETPLAY_MSG_JOIN_ACK);
    msg.u.join.frame_skip = handle->frame_skip;
    msg.u.join.time       = htonl(handle->basetime);
    strncpy(msg.u.join.game_name, handle->game_name, MAX_GAME_NAME - 1);
    msg.u.join.game_name[MAX_GAME_NAME - 1] = '\0';
    
    NLOG("send join ack for %s with basetime:%s", handle->game_name, ctime(&handle->basetime));
    
    int ret = handle->send_pkt_data(handle, &msg);
    NLOG("send join ack send_pkt_data returned %d", ret);
    return ret;
}

int netplay_send_disconnect(netplay_t *handle){
    netplay_msg_t msg;
    memset(&msg, 0, sizeof(msg));

    if (!handle->has_connection)
        return 0;
        
    uint32_t uid  = __sync_add_and_fetch(&handle->packet_uid, 1);
    msg.packetid  = htonl(uid);
    msg.msg_type  = htonl(NETPLAY_MSG_DISCONNECT);
    
    NLOG("sending disconnect message to peer (5 bursts to ensure delivery)");
    int ret = 0;
    /* Send multiple times: UDP is lossy and we are about to tear down the
     * socket, so we want the peer to receive at least one copy.           */
    for (int i = 0; i < 5; i++) {
        ret = handle->send_pkt_data(handle, &msg);
        usleep(10000); /* 10 ms */
    }
    return ret;
}

/* ── netplay_pre_frame_net ─────────────────────────────────────────────── */

void netplay_pre_frame_net(netplay_t *handle)
{
    if (!handle->has_connection || !handle->has_begun_game) return;

    if (handle->frame < handle->frame_skip)
        return; /* First frame_skip frames do not need a sync point.       */

    pthread_mutex_lock(&handle->sync_mutex);

    if (!IS_SYNCED(handle))
    {
        handle->timeout_cnt++;

        /* ── cond_timedwait-based sync wait ─────────────────────────────
         * FIX: replaces the original 1 ms usleep spin-poll which burned a
         * full CPU core and starved the network thread on Android.
         * We wake immediately when the network thread signals sync_cond,
         * and retransmit every RETRANSMIT_MS ms to drive the peer's ACK.  */

        /* Overall deadline (30 s, extended for early frames).             */
        struct timespec deadline;
        clock_gettime(CLOCK_REALTIME, &deadline);
        long timeout_ms = SYNC_TIMEOUT_MS;
        if (handle->frame < 10) timeout_ms *= 8; /* extra time at start   */
        deadline.tv_sec  += timeout_ms / 1000;
        deadline.tv_nsec += (timeout_ms % 1000) * 1000000LL;
        if (deadline.tv_nsec >= 1000000000LL) {
            deadline.tv_sec++;
            deadline.tv_nsec -= 1000000000LL;
        }

        int warn_paused = 0;
        int inner_iter = 0;

        while (!IS_SYNCED(handle))
        {
            if (!handle->has_connection) break;

            /* Unlock before I/O; the network thread must not be blocked
             * by the game thread holding the mutex while sending.         */
            pthread_mutex_unlock(&handle->sync_mutex);
            netplay_send_data(handle);          /* retransmit              */
            pthread_mutex_lock(&handle->sync_mutex);

            if (IS_SYNCED(handle) || !handle->has_connection) break;

            struct timespec wake;
            clock_gettime(CLOCK_REALTIME, &wake);
            /* Fast burst: retransmit 3x at 8ms before falling back to 24ms.
             * Reduces stutter when the bottleneck is our own lost packet.   */
            long iter_ms = (inner_iter < 3) ? 8 : RETRANSMIT_MS;
            inner_iter++;
            wake.tv_nsec += iter_ms * 1000000LL;
            if (wake.tv_nsec >= 1000000000LL) {
                wake.tv_sec++;
                wake.tv_nsec -= 1000000000LL;
            }
            /* Clamp to overall deadline so we never overshoot.           */
            if (wake.tv_sec > deadline.tv_sec ||
                (wake.tv_sec == deadline.tv_sec &&
                 wake.tv_nsec > deadline.tv_nsec)) {
                wake = deadline;
            }

            /* Sleep until the network thread signals us or wake expires.  */
            pthread_cond_timedwait(&handle->sync_cond, &handle->sync_mutex, &wake);

            /* Peer-paused timeout extension:
             * If the peer pauses the game, we extend the deadline indefinitely
             * as long as we keep receiving network activity (silence < 10s).
             * If the network dies while paused, the silence check forces the
             * deadline to expire, preventing an infinite hang. */
            if (handle->is_peer_paused) {
                uint32_t now_ms = netplay_get_ticks_ms();
                uint32_t silence_ms = now_ms - handle->last_recv_time_ms;

                if (!warn_paused) {
                    warn_paused = 1;
                    pthread_mutex_unlock(&handle->sync_mutex);
                    if (handle->netplay_warn)
                        handle->netplay_warn((char*)"TOAST:Netplay: Peer is paused, please wait...");
                    myosd_droid_netplay_set_exitPause(1);
                    pthread_mutex_lock(&handle->sync_mutex);
                }

                if (silence_ms < 10000) {
                    /* Peer is paused but still reachable: extend deadline and keep waiting. */
                    clock_gettime(CLOCK_REALTIME, &deadline);
                    deadline.tv_sec  += SYNC_TIMEOUT_MS / 1000;
                    deadline.tv_nsec += (SYNC_TIMEOUT_MS % 1000) * 1000000LL;
                    if (deadline.tv_nsec >= 1000000000LL) {
                        deadline.tv_sec++;
                        deadline.tv_nsec -= 1000000000LL;
                    }
                    continue;  /* continue ONLY when network is alive */
                }
                /* else: silence >= 10s, network is dead.
                 * Do NOT continue. Fall through to the deadline check below. */
            }

            /* Check whether the overall deadline has expired.            */
            struct timespec now_ts;
            clock_gettime(CLOCK_REALTIME, &now_ts);
            if (now_ts.tv_sec > deadline.tv_sec ||
                (now_ts.tv_sec == deadline.tv_sec &&
                 now_ts.tv_nsec >= deadline.tv_nsec)) {
                NLOG("SYNC TIMEOUT: frame=%u target=%u peer=%u peer_peer=%u",
                     handle->frame, handle->target_frame,
                     handle->peer_frame, handle->peer_peer_frame);
                break;
            }
        }
    }
    else
    {
        handle->timeout_cnt = 0;
    }

    /* ── Post-wait: check final state ──────────────────────────────────── */

    if (!IS_SYNCED(handle))
    {
        /* Still not synced after the timeout – declare hangup.           */
        pthread_mutex_unlock(&handle->sync_mutex);
        if (handle->has_connection) {
            handle->has_connection = 0;
            netplay_warn_hangup(handle);
        }
        return;
    }

    /* Commit the peer state and our own snapshot for this frame.
     * Both accesses are under the mutex so no torn reads.                */
    if (handle->frame == handle->target_frame)
    {
        handle->state      = handle->state_tmp;
        handle->peer_state = handle->peer_state_tmp;
    }

    pthread_mutex_unlock(&handle->sync_mutex);

    /* ── Auto-frameskip RTT check (host only, every 5 s) ──────────────── */
    if (handle->player1) {
        uint32_t now = netplay_get_ticks_ms();
        if (now - handle->rtt_update_time > 5000) {

            /* Snapshot RTT accumulators and reset the peak/min counter.      */
            pthread_mutex_lock(&handle->sync_mutex);
            uint32_t frtt = handle->fast_rtt;
            uint32_t srtt = handle->smoothed_rtt;
            uint32_t mrtt = handle->max_rtt_interval;
            uint32_t minrtt = handle->min_rtt_window;
            if (minrtt == 0) minrtt = srtt; /* fallback if no packets received */
            
            uint32_t n1_count = handle->recovery_n1_count;
            handle->recovery_n1_count = 0;
            
            /* We no longer reset max_rtt_interval to srtt. We use Decaying Peak. */
            /* Removed min_rtt_window = 0 reset, as we now use a decaying minimum */
            pthread_mutex_unlock(&handle->sync_mutex);

            /* Compromise Jitter Buffer:
             * Min RTT minimizes input lag but causes micro-stuttering on Hotspots.
             * Peak RTT (mrtt) absorbs stuttering but causes excessive input lag.
             * By calculating the Jitter Envelope (Peak - Min) and adding 50% of it 
             * to the baseline, the auto-frameskip naturally finds the "sweet spot"
             * (e.g. FrameSkip 4-6 on unstable mobile networks), balancing smoothness
             * and input lag without violent oscillations. */
            uint32_t jitter_envelope = (mrtt > minrtt) ? (mrtt - minrtt) : 0;
            uint32_t rtt_to_use = minrtt + (jitter_envelope / 2);
            
            NLOG("Ping Trace: Min=%u Fast=%u Peak=%u FS=%u N1_recv=%u",
                 minrtt, frtt, mrtt, handle->frame_skip, n1_count);

            if (rtt_to_use > 0 && handle->is_auto_frameskip) {

                uint32_t our_lag = handle->target_frame - handle->peer_peer_frame;
                if (our_lag > handle->frame_skip * 2) {
                    NLOG("WARN: Asymmetric loss detected - peer has only ACKed up to frame %u "
                         "(our target=%u, lag=%u frames)", 
                         handle->peer_peer_frame, handle->target_frame, our_lag);
                }

                pthread_mutex_lock(&handle->sync_mutex);
                int epoch_pending = (handle->frameskip_epoch_frame != 0);
                pthread_mutex_unlock(&handle->sync_mutex);

                if (!epoch_pending) {
                    uint32_t optimal = rtt_to_use / 16;
                    
                    /* Loss compensation: packet loss is independent of RTT and requires
                     * extra buffer depth. Add frames proportional to recovery frequency. */
                    if      (n1_count >= 5)  optimal += 2;   /* heavy loss rate: add 2       */
                    else if (n1_count >= 2)  optimal += 1;   /* moderate loss: add 1         */

                    if (optimal < 2)  optimal = 2;
                    if (optimal > 10) optimal = 10;
                    if (optimal % 2 != 0) optimal++;

                    NLOG("Auto-Frameskip: rtt_to_use=%u optimal=%u current=%u",
                         rtt_to_use, optimal, handle->frame_skip);

                    if (handle->frame_skip != optimal) {
                        uint32_t offset = (optimal > handle->frame_skip) ? 30 : 120;
                        pthread_mutex_lock(&handle->sync_mutex);
                        handle->frameskip_epoch_frame = handle->target_frame + offset;
                        handle->frameskip_epoch_value = (uint8_t)optimal;
                        pthread_mutex_unlock(&handle->sync_mutex);
                        NLOG("Auto-Frameskip: scheduled epoch frameskip=%u at frame=%u",
                             optimal, handle->target_frame + offset);
                    }
                }
            }
            handle->rtt_update_time = now;
        }
    }
}

/* ── netplay_post_frame_net ────────────────────────────────────────────── */

void netplay_post_frame_net(netplay_t *handle)
{
    if (!handle->has_connection || !handle->has_begun_game) return;

    if (handle->frame == handle->target_frame)
    {
        pthread_mutex_lock(&handle->sync_mutex);

        /* Emergency frameskip: bump lag compensation if we keep timing out. */
        /* CRITICAL FIX: Disabled Emergency Frameskip for Hotspots.
         * A jitter spike causes consecutive timeouts. Bumping the frameskip here
         * artificially makes the game run at 6 FPS long after the spike ends.
         * It is much better to just freeze temporarily and resume at full speed! */
        /*
        if (handle->timeout_cnt > 10 &&
            handle->frame_skip <= 10  &&
            handle->is_auto_frameskip &&
            handle->player1           &&
            handle->frameskip_epoch_frame == 0)
        {
            handle->frameskip_epoch_frame = handle->target_frame + 30;
            handle->frameskip_epoch_value = handle->frame_skip + 2;
            if (handle->frameskip_epoch_value > 10)
                handle->frameskip_epoch_value = 10;
            NLOG("Emergency frameskip: scheduled epoch=%u",
                 handle->frameskip_epoch_value);
        }
        */
        handle->timeout_cnt = 0;

        /* Apply epoch if the scheduled frame has arrived.                */
        if (handle->frameskip_epoch_frame > 0 &&
            handle->frame >= handle->frameskip_epoch_frame) {
            handle->frame_skip            = handle->frameskip_epoch_value;
            handle->frameskip_epoch_frame = 0;
            
            /* Claude Fix #2: Invalidate pre-fetch because frame_skip changed.
             * The pre-fetch was calculated with the OLD frame_skip offset. */
            handle->peer_next_frame       = 0;
            
            NLOG("Applied Epoch frameskip: %u (pre-fetch invalidated)", handle->frame_skip);
        }

        /* Update N-1 history BEFORE advancing the target frame.
         * Must perfectly match the unmodified state sent to the peer. */
        handle->prev_target_frame = handle->target_frame;
        handle->prev_state_sent   = handle->state_tmp;

        /* Advance the target frame.                                      */
        uint32_t new_target    = handle->frame + handle->frame_skip;
        handle->target_frame   = new_target;

        /* Promote the pre-fetched peer frame if it matches the new target.*/
        if (new_target == handle->peer_next_frame) {
            handle->peer_frame     = handle->peer_next_frame;
            handle->peer_state_tmp = handle->peer_next_state_tmp;
            handle->peer_next_frame= 0;
            /* Wake pre_frame_net if it already entered the wait loop.    */
            pthread_cond_signal(&handle->sync_cond);
        }

        pthread_mutex_unlock(&handle->sync_mutex);

        /* Read local joystick inputs for the new target_frame.
         * Done outside the lock: these fields are game-thread-only.      */
        if (handle->frame != 0)
        {
            handle->state_tmp.digital     = myosd_netplay_joystick_read(0);
            handle->state_tmp.analog_x    = myosd_netplay_joystick_read_analog(0, 'x');
            handle->state_tmp.analog_y    = myosd_netplay_joystick_read_analog(0, 'y');
            handle->state_tmp.analog_rx   = myosd_netplay_joystick_read_analog(0, 'X');
            handle->state_tmp.analog_ry   = myosd_netplay_joystick_read_analog(0, 'Y');
            handle->state_tmp.analog_lz   = myosd_netplay_joystick_read_analog(0, 'l');
            handle->state_tmp.analog_rz   = myosd_netplay_joystick_read_analog(0, 'r');
            handle->state_tmp.mouse_status = myosd_netplay_mouse_read(0);
            handle->local_abs_mouse_x    += myosd_netplay_mouse_read_analog(0, 'x');
            handle->local_abs_mouse_y    += myosd_netplay_mouse_read_analog(0, 'y');
            handle->state_tmp.mouse_x     = handle->local_abs_mouse_x;
            handle->state_tmp.mouse_y     = handle->local_abs_mouse_y;
            handle->state_tmp.lightgun_x  = myosd_netplay_lightgun_read_analog(0, 'x');
            handle->state_tmp.lightgun_y  = myosd_netplay_lightgun_read_analog(0, 'y');
            handle->state_tmp.ext         = myosd_droid_netplay_get_ext_status();
        }

        /* netplay_send_data also updates prev_target_frame / prev_state_sent. */
        if (!netplay_send_data(handle))
        {
            handle->has_connection = 0;
            netplay_warn_hangup(handle);
            return;
        }
    }

    handle->frame++;
}

void netplay_ui_set_connection(netplay_t *handle, int value)
{
    if (handle) {
        if (value == 0 && handle->has_connection) {
            netplay_send_disconnect(handle);
        }
        handle->has_connection = value;
    }
}

void netplay_ui_set_delay(netplay_t *handle, int value)
{
    int original_value = value;
    if (value > 0) {
        value = value * 2; // Map UI visual frames to internal polling ticks (2 polls per frame)
    }
    static int frame_delay = -1;
    if (handle)
    {
        if (!handle->has_joined)
        {
            // Before connection: apply directly to frame_skip so it gets
            // sent to the Client in the JOIN_ACK packet.
            if (value > 0) {
                handle->frame_skip = value;
                handle->peer_frame_skip = value;
                handle->is_auto_frameskip = 0;
            } else {
                handle->is_auto_frameskip = 1;
            }
        }
        else if (handle->player1 && value != frame_delay)
        {
            // Mid-game: only server (player1) can trigger the handshake.
            // Client ignores changes to avoid asymmetric state.
            if (value == 0)
            {
                handle->is_auto_frameskip = 1;
            }
            else
            {
                handle->is_auto_frameskip = 0;
                if (handle->frame_skip != value) {
                    uint32_t epoch_offset = (value > handle->frame_skip) ? 30 : 120;
                    handle->frameskip_epoch_frame = handle->target_frame + epoch_offset;
                    handle->frameskip_epoch_value = value;
                }
            }
        }
    }
    frame_delay = value;
}
