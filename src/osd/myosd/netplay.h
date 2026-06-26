// license:BSD-3-Clause
// copyright-holders: David Valdeita (Seleuco) 
/***************************************************************************

    netplay.h

    MAME4droid Lock-Step Netplay Architecture

***************************************************************************/

#ifndef netplay_h
#define netplay_h

#include <stdint.h>
#include <time.h>
#include <pthread.h>  /* required for pthread_mutex_t / pthread_cond_t */

#include "myosd.h"


    
    typedef enum {
        NETPLAY_TYPE_SKT = 1,
        NETPLAY_TYPE_GAMEKIT,
    } netplay_impl_type;

#ifndef MAX_GAME_NAME
#define MAX_GAME_NAME 64
#endif
    
#pragma pack(push, 1)
    typedef struct netplay_state{
        uint32_t digital;
        float analog_x;
        float analog_y;
        float analog_rx;
        float analog_ry;
        float analog_lz;
        float analog_rz;
        uint16_t ext;
        float mouse_x;
        float mouse_y;
        uint32_t mouse_status;
        float lightgun_x;
        float lightgun_y;
    }netplay_state_t;
    
    typedef enum {
        NETPLAY_MSG_DATA = 1,
        NETPLAY_MSG_JOIN,
        NETPLAY_MSG_JOIN_ACK,
        NETPLAY_MSG_DISCONNECT
    } netplay_msg_type;
    
    typedef struct netplay_msg_join {
        uint8_t frame_skip;
        uint32_t time;
        char game_name[MAX_GAME_NAME];
    }netplay_msg_join_t;
    
    typedef struct netplay_msg_data {
        uint8_t is_peer_paused;
        uint32_t peer_frame;
        netplay_state_t peer_state_tmp;
        /* N-1 input piggybacking: every packet also carries the PREVIOUS frame's
         * inputs.  If peer_frame-1 packet was lost, the receiver recovers it
         * from (peer_frame_prev / peer_state_prev) without stalling.          */
        uint32_t peer_frame_prev;
        netplay_state_t peer_state_prev;
        /* ------------------------------------------------------------------- */
        uint32_t peer_peer_frame;
        uint8_t peer_frame_skip;
        uint32_t timestamp;
        uint32_t echo_timestamp;
        uint32_t frameskip_epoch_frame;
        uint8_t frameskip_epoch_value;
    }netplay_msg_data_t;
    
    typedef struct netplay_msg{
        uint32_t packetid;
        uint32_t msg_type;
        union {
            netplay_msg_join_t join;
            netplay_msg_data_t data;
        }u;
    }netplay_msg_t;
#pragma pack(pop)
    
    typedef struct netplay
    {
        netplay_impl_type type;
        
        unsigned player1;
        
        volatile int has_connection;
        int has_joined;
        volatile int has_begun_game;
        volatile int is_peer_paused;
        int is_auto_frameskip;
        int new_frameskip_set;
        
        char game_name[MAX_GAME_NAME];
        
        unsigned timeout_cnt;
        uint32_t packet_uid;        /* always modified via __sync_add_and_fetch */
        uint32_t recv_packet_uid;   /* network-thread-only                       */
        uint32_t last_recv_time_ms; /* network-thread-only                       */
        
        float local_abs_mouse_x;
        float local_abs_mouse_y; 
        
        netplay_state_t state;
        netplay_state_t peer_state;
        
        netplay_state_t state_tmp;
        netplay_state_t peer_state_tmp;
        netplay_state_t peer_next_state_tmp;
        
        uint32_t frame;
        volatile uint32_t target_frame;
        volatile uint32_t peer_frame;
        volatile uint32_t peer_next_frame;
        volatile uint32_t peer_peer_frame;
        
        uint32_t frame_skip;
        volatile uint32_t peer_frame_skip;
        
        uint32_t frameskip_epoch_frame;
        uint8_t frameskip_epoch_value;
        uint32_t last_epoch_received;
        
        uint32_t last_peer_timestamp;
        uint32_t smoothed_rtt;
        uint32_t fast_rtt;
        uint32_t max_rtt_interval;
        uint32_t min_rtt_window;
        uint32_t rtt_update_time;

        /* N-1 history: the most recently transmitted frame number and its
         * (already-filtered) inputs.  Piggybacked in every DATA packet so
         * the peer can recover a lost frame without stalling.               */
        uint32_t        prev_target_frame;
        netplay_state_t prev_state_sent;

        /* Loss counters for auto-frameskip                                  */
        uint32_t        recovery_n1_count;
        
        time_t basetime;
        
        void *impl_data;

        /* ── Synchronisation primitives ──────────────────────────────────────
         * sync_mutex: serialises all cross-thread field writes listed above
         *             (peer_frame, peer_state_tmp, epoch fields, RTT fields,
         *              is_peer_paused).
         * sync_cond:  signalled by the network thread whenever peer_frame is
         *             updated, waking the game thread's lockstep wait instead
         *             of the old 1 ms busy-poll loop.                        */
        pthread_mutex_t sync_mutex;
        pthread_cond_t  sync_cond;

        int (*read_pkt_data)(struct netplay *,netplay_msg_t *);
        int (*send_pkt_data)(struct netplay *,netplay_msg_t *);
        void (*netplay_warn)(char *);
        
    } netplay_t;
    
    
    netplay_t * netplay_get_handle();
    void netplay_warn_hangup(netplay_t *handle);
    int  netplay_read_data(netplay_t *handle);
    int  netplay_send_data(netplay_t *handle);
    int  netplay_send_join(netplay_t *handle);
    int  netplay_send_join_ack(netplay_t *handle);
    int  netplay_send_disconnect(netplay_t *handle);
    int  netplay_init_handle(netplay_t *handle);
    void netplay_pre_frame_net(netplay_t *handle);
    void netplay_post_frame_net(netplay_t *handle);
    
    void netplay_ui_set_connection(netplay_t *handle, int value);
    void netplay_ui_set_delay(netplay_t *handle, int value);
    
#endif
