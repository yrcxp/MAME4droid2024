/*
 * This file is part of MAME4droid.
 *
 * Copyright (C) 2026 David Valdeita (Seleuco)
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 2 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
 * General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, see <http://www.gnu.org/licenses>.
 *
 * Linking MAME4droid statically or dynamically with other modules is
 * making a combined work based on MAME4droid. Thus, the terms and
 * conditions of the GNU General Public License cover the whole
 * combination.
 *
 * In addition, as a special exception, the copyright holders of MAME4droid
 * give you permission to combine MAME4droid with free software programs
 * or libraries that are released under the GNU LGPL and with code included
 * in the standard release of MAME under the MAME License (or modified
 * versions of such code, with unchanged license). You may copy and
 * distribute such a system following the terms of the GNU GPL for MAME4droid
 * and the licenses of the other code concerned, provided that you include
 * the source code of that other code when and as the GNU GPL requires
 * distribution of source code.
 *
 * Note that people who make modified versions of MAME4idroid are not
 * obligated to grant this special exception for their modified versions; it
 * is their choice whether to do so. The GNU General Public License
 * gives permission to release a modified version without this exception;
 * this exception also makes it possible to release a modified version
 * which carries forward this exception.
 *
 * MAME4droid is dual-licensed: Alternatively, you can license MAME4droid
 * under a MAME license, as set out in http://mamedev.org/
 */

package com.seleuco.mame4droid.helpers;

import android.content.Context;
import android.telephony.TelephonyManager;
import android.util.Log;

import com.seleuco.mame4droid.Emulator;
import com.seleuco.mame4droid.MAME4droid;

import java.util.List;
import java.util.Locale;

/**
 * One host's stay on the public board: publish the game, keep it alive by
 * polling, hand the peer's tuple to the punching machinery, take the room down
 * afterwards. All best-effort -- a board that is off or unreachable just never
 * publishes, and the user shares an IP by hand as before. Worker threads only.
 */
public class LobbySession {

    private static final String TAG = "MAME4droid_Netplay";

    private final MAME4droid mm;
    private final String base;
    private final int proto;
    private final String app;

    private volatile String roomId;
    private volatile String roomToken;
    private volatile int pollSeconds = 3;

    /* Published as a game already under way. */
    private volatile boolean playing = false;
    private volatile boolean verified;
    private volatile int viewers;
    private volatile int lastPublishStatus = 0;
    private volatile String lastPublishError = "";

    public LobbySession(MAME4droid mm) {
        this.mm = mm;
        this.base = mm.getPrefsHelper().getNetplayLobbyUrl();
        this.proto = Emulator.netplayGetProtocolVersion();
        this.app = appVersion(mm);
    }

    /** Whether to bother the board at all: switched on, told about it once,
     *  and a .so new enough to tell us its protocol. Publishing hands our
     *  address to whoever joins, so the consent gate is not optional. */
    public static boolean isUsable(MAME4droid mm) {
        return mm.getPrefsHelper().isNetplayLobbyEnabled()
                && mm.getPrefsHelper().isNetplayLobbyConsentGiven()
                && Emulator.netplayGetProtocolVersion() > 0;
    }

    public boolean isPublished() {
        return roomId != null;
    }

    /** The room we published, while it lasts: it is what ties our own report
     *  to the other player's. */
    public String getRoomId() {
        return roomId;
    }

    /** HTTP status of the last publish attempt, 0 when it never got out.
     *  It decides how soon retrying is worth it: nobody answered means the
     *  instance is still waking, while a refusal came from our own server. */
    public int getLastPublishStatus() {
        return lastPublishStatus;
    }

    /** True when the last attempt never reached the lobby: offline, or the
     *  host's own 503 while a sleeping server boots. */
    /**
     * True when the board refused us because this address already holds as
     * many rooms as it may. Not a rate limit and not a cold start, even
     * though it arrives as the same 429: nothing about waiting fixes it
     * until one of those rooms ends.
     */
    public boolean lastPublishHitRoomQuota() {
        return "too_many_rooms".equals(lastPublishError);
    }

    public boolean lastPublishWasUnreachable() {
        return lastPublishStatus == 0 || lastPublishStatus == 503;
    }

    /**
     * Seconds between heartbeats. A room waiting for someone is polled fast,
     * since that poll is how the host learns whom to punch back at. A drop-in
     * room stays up for the whole game, where three seconds would be 1200
     * calls an hour: ten beats the room's own TTL six times over instead.
     */
    public int getPollSeconds() {
        return playing ? Math.max(pollSeconds, DROP_IN_POLL_SECONDS) : pollSeconds;
    }

    private static final int DROP_IN_POLL_SECONDS = 10;

    /** False when the server could not confirm our address is really ours;
     *  legitimate on mobile, and the board shows those rooms without a badge. */
    public boolean isVerified() {
        return verified;
    }

    /** How many people are looking at the board right now. */
    public int getViewers() {
        return viewers;
    }

    public String getServerUrl() {
        return base;
    }

    /**
     * Advertise the running game. Call after netplayInit has returned, which
     * is when STUN has actually produced our public tuple.
     *
     * @return true if the room is on the board.
     */
    public boolean publish(String game, int mode, int delay, boolean plugins,
                           List<String> lanAddresses, boolean upnpMapped, String pin,
                           boolean playing) {
        if (proto <= 0 || game == null || game.length() == 0) return false;

        String info = Emulator.netplayGetPublicAddr();
        String[] tuples = publicTuples(info);
        if (tuples[0] == null) {
            /* No public tuple means STUN failed or was skipped: nobody off this
             * LAN could reach us, so there is nothing worth advertising -- and
             * checked first, so a retry costs no request at all. */
            if (LobbyClient.DEBUG) Log.d(TAG, "lobby: no public address, not publishing");
            return false;
        }

        /* A session killed mid-flight (app swiped away, network dropped on the
         * DELETE) leaves its room on the board for up to a minute. Two rooms
         * from one device look identical to everyone else, so a joiner can
         * pick the dead one and wait forever while the live host, polling a
         * room nobody claimed, never hears a thing. */
        dropPreviousRoom();

        LobbyClient.Created created = LobbyClient.create(base, proto, app, game, mode, delay,
                plugins, tuples[0], tuples[1], lanAddresses, natOf(info, upnpMapped),
                country(mm), pin, playing);

        lastPublishStatus = created.status;
        lastPublishError = (created.error != null) ? created.error : "";

        if (!created.ok() || created.id == null || created.id.length() == 0) {
            if (LobbyClient.DEBUG) Log.d(TAG, "lobby: publish refused (status " + created.status + ")");
            return false;
        }

        roomId = created.id;
        roomToken = created.token;
        verified = created.verified;
        mm.getPrefsHelper().setNetplayLastRoom(roomId + "," + roomToken);
        if (created.pollSeconds > 0) pollSeconds = created.pollSeconds;
        this.playing = playing;
        if (LobbyClient.DEBUG) Log.d(TAG, "lobby: published as " + roomId + (verified ? " (verified)" : "")
                + (pin != null ? " (private)" : ""));
        return true;
    }

    /**
     * One heartbeat. Returns the peer once somebody claims the room, or null
     * while nobody has. Keeps returning the same peer afterwards, so a lost
     * answer costs a poll interval and nothing else.
     */
    public LobbyClient.Endpoint poll() {
        if (roomId == null) return null;

        LobbyClient.PollResult result = LobbyClient.poll(base, roomId, roomToken);
        viewers = result.viewers;

        /* Field diagnosis: without this a host that polls happily and is simply
         * never handed a peer looks exactly like one that is not polling at
         * all. Cheap at one line every few seconds. */
        if (LobbyClient.DEBUG) Log.d(TAG, "lobby: poll " + roomId + " -> status=" + result.status
                + " state=" + result.state + " viewers=" + result.viewers
                + " peer=" + (result.peer != null ? result.peer.publicAddr : "none"));

        if (result.status == 404) {
            /* The instance recycled and took the room with it. Publishing
             * again is the honest fix, and the user never needs to know. */
            if (LobbyClient.DEBUG) Log.d(TAG, "lobby: room gone, dropping it");
            roomId = null;
            roomToken = null;
            return null;
        }
        return result.ok() ? result.peer : null;
    }

    /**
     * Point the hole-punching at the peer we were handed. Same rule the manual
     * path uses: one public address between us means one router, so dial the
     * LAN side; otherwise punch toward the public tuple.
     *
     * @return true if a target was armed.
     */
    public static boolean aimAt(LobbyClient.Endpoint peer, int fallbackPort) {
        if (peer == null) return false;

        String target = null;
        if (peer.sameSite && peer.lan.length > 0)
            target = peer.lan[0];
        else if (peer.publicAddr != null)
            target = peer.publicAddr;
        else if (peer.publicAlt != null)
            target = peer.publicAlt;

        if (target == null) return false;

        String[] hostPort = splitHostPort(target);
        int port = fallbackPort;
        if (hostPort[1] != null) {
            try {
                port = Integer.parseInt(hostPort[1]);
            } catch (Exception e) {
                /* keep the settings port */
            }
        }
        if (hostPort[0] == null || hostPort[0].length() == 0) return false;

        Emulator.netplaySetPunchAddr(hostPort[0], port);
        if (LobbyClient.DEBUG) Log.d(TAG, "lobby: aiming at peer (" + (peer.sameSite ? "same site" : "internet") + ")");
        return true;
    }

    /** Take the room off the board. Safe to call more than once. */
    public void close() {
        String id = roomId;
        String token = roomToken;
        roomId = null;
        roomToken = null;
        if (id == null) return;

        LobbyClient.delete(base, id, token);
        mm.getPrefsHelper().setNetplayLastRoom("");
        if (LobbyClient.DEBUG) Log.d(TAG, "lobby: room " + id + " withdrawn");
    }

    /** Withdraw whatever this device left behind last time, before publishing
     *  again. Failing is fine: the room dies on its own within the minute. */
    private void dropPreviousRoom() {
        String stored = mm.getPrefsHelper().getNetplayLastRoom();
        if (stored == null || stored.length() == 0) return;

        mm.getPrefsHelper().setNetplayLastRoom("");
        String[] parts = stored.split(",");
        if (parts.length != 2) return;

        LobbyClient.delete(base, parts[0], parts[1]);
        if (LobbyClient.DEBUG) Log.d(TAG, "lobby: cleared leftover room " + parts[0]);
    }

    /** Report how a session ended. Fire and forget, never blocks the caller. */
    public void report(final String game, final String role, final String outcome,
                       final LobbyClient.Nat self, final LobbyClient.Nat peer,
                       final String path, final long waitMs, final String peerCountry,
                       final int mode, final int delay, final long playMs,
                       final int rttMs, final int jitterMs,
                       final int rttMinMs, final int rttMaxMs, final boolean locked,
                       final String room, final boolean dropIn) {
        if (proto <= 0) return;
        final String here = country(mm);
        new Thread(new Runnable() {
            public void run() {
                LobbyClient.telemetry(base, proto, app, game, role, outcome,
                        self, peer, path, waitMs, here, peerCountry, mode, delay, playMs,
                        rttMs, jitterMs, rttMinMs, rttMaxMs, locked, room, dropIn);
            }
        }).start();
    }

    /**
     * Primary and alternate public tuples out of "ip:port|pp=x|sym=y[|alt=..]".
     * A dual-stack host gets a v6 primary with the v4 riding along in "alt=";
     * dropping that half leaves v4-only peers unable to reach a room they can
     * see on the board.
     *
     * @return {primary, alt}, either of which may be null.
     */
    public static String[] publicTuples(String info) {
        String[] out = new String[]{null, null};
        if (info == null || info.length() == 0) return out;

        String[] parts = info.split("\\|");
        if (parts.length > 0 && parts[0].length() > 0) out[0] = parts[0];
        for (String part : parts)
            if (part.startsWith("alt=")) out[1] = part.substring(4);
        return out;
    }

    /**
     * NAT quality flags out of the same string, plus our UPnP result. sym is
     * reported as measured -- a true fact about our IPv4 NAT, not a verdict
     * on the pairing. Deciding what it means belongs in chanceOf(), which can
     * see both sides.
     */
    public static LobbyClient.Nat natOf(String info, boolean upnpMapped) {
        if (info == null) return new LobbyClient.Nat(false, false, upnpMapped, false);

        /* "[2001:db8::1]:2080|..." is a v6 primary; "1.2.3.4:2080|..." is v4. */
        boolean v6 = info.startsWith("[");
        return new LobbyClient.Nat(info.contains("sym=1"), info.contains("pp=1"),
                upnpMapped, v6);
    }

    /**
     * Keep what STUN just told us: without it the board has nothing to judge
     * a room against and calls everything promising. Stamped with the family
     * it was taken under, since STUN does not re-run when that setting
     * changes and the old reading may describe a road we no longer take.
     */
    public static void rememberNat(MAME4droid mm, String info, boolean upnpMapped) {
        if (info == null || info.length() == 0) return;
        LobbyClient.Nat nat = natOf(info, upnpMapped);
        mm.getPrefsHelper().setNetplayLastNat(
                (nat.sym ? "1" : "0") + "," + (nat.pp ? "1" : "0") + ","
                        + (nat.upnp ? "1" : "0") + "," + (nat.v6 ? "1" : "0") + ","
                        + mm.getPrefsHelper().getNetplayIpProtocol());
    }

    /**
     * Our NAT, as far as it still applies. STUN runs once per session, so a
     * measurement predates any later change of family -- and which half
     * survives is not all-or-nothing: a symmetric IPv4 NAT is a fact about
     * the carrier, while the v6 flag stops mattering if we go IPv4 only.
     */
    public static LobbyClient.Nat lastKnownNat(MAME4droid mm) {
        String[] flags = mm.getPrefsHelper().getNetplayLastNat().split(",");
        if (flags.length < 5) return null;

        int measured;
        try {
            measured = Integer.parseInt(flags[4]);
        } catch (Exception e) {
            return null;
        }
        int family = mm.getPrefsHelper().getNetplayIpProtocol();

        /* A v4 leg ran unless the session was IPv6 only, and what it found
         * matters unless we are IPv6 only now. Same shape for the v6 leg. */
        boolean v4applies = measured != 1 && family != 1;
        boolean v6applies = measured != 0 && family != 0;
        if (!v4applies && !v6applies) return null;

        /* The native string is that same measurement, fresher: it survives
         * until the next netplayInit, beside the stamp read above. */
        String info = Emulator.netplayGetPublicAddr();
        LobbyClient.Nat nat = (info != null && info.length() > 0)
                ? natOf(info, UpnpHelper.isMapped())
                : new LobbyClient.Nat("1".equals(flags[0]), "1".equals(flags[1]),
                        "1".equals(flags[2]), "1".equals(flags[3]));

        return new LobbyClient.Nat(v4applies && nat.sym, v4applies && nat.pp,
                nat.upnp, v6applies && nat.v6);
    }

    /**
     * How likely two NATs are to connect, using the rule the field notes
     * state: punching works unless a side is symmetric, and IPv6 has no NAT
     * to defeat at all.
     *
     * @return 2 good, 1 uncertain, 0 hopeless without a relay.
     */
    public static int chanceOf(LobbyClient.Nat self, LobbyClient.Nat other) {
        if (other == null) return 1;

        /* Our own NAT is only known once STUN has run on a game socket, so a
         * device that has not hosted or joined yet knows nothing about itself.
         * Judging on the other side alone beats painting the whole board amber
         * and beats showing two phones different colours for one room. A UPnP
         * claim on its own no longer counts as good news here either. */
        if (self == null)
            return other.sym ? 1 : 2;

        /* Both on IPv6: that is the route they will take and it has no NAT on
         * it, so the v4 verdict describes a road neither of them is using.
         * Two symmetric CGNAT phones with working v6 connect happily, and
         * calling that hopeless refused a pairing that works.  Field-found. */
        if (self.v6 && other.v6) return 2;

        /* Neither side has a NAT to defeat: this is the one case we can
         * promise, because it needs nothing to be true that we have not
         * measured ourselves. */
        if (!self.sym && !other.sym) return 2;

        /* Past here a side is symmetric, so the pairing hangs on the other
         * being reachable from outside -- and a UPnP mapping is a claim, not
         * a measurement. It says the router accepted the request; the router
         * this was found on installs the rule and its firewall drops the
         * traffic anyway, mapped and verified. Enough to lift a hopeless
         * pairing to a maybe, never enough to call it good. */
        if (self.upnp || other.upnp) return 1;

        /* Both mappings move per destination and nothing is forwarded: there
         * is no tuple either side could aim at. */
        if (self.sym && other.sym) return 0;

        return 1;
    }

    /** ISO-2 from the SIM, falling back to the locale. Declared, not derived:
     *  the server never looks an address up, it just shows the flag. */
    public static String country(MAME4droid mm) {
        try {
            TelephonyManager phone = (TelephonyManager) mm.getSystemService(Context.TELEPHONY_SERVICE);
            String iso = (phone != null) ? phone.getNetworkCountryIso() : null;
            if (iso != null && iso.length() == 2) return iso.toUpperCase(Locale.US);
        } catch (Exception e) {
            /* no telephony on this device */
        }
        String iso = Locale.getDefault().getCountry();
        return (iso != null && iso.length() == 2) ? iso.toUpperCase(Locale.US) : null;
    }

    public static String appVersion(MAME4droid mm) {
        try {
            return mm.getPackageManager().getPackageInfo(mm.getPackageName(), 0).versionName;
        } catch (Exception e) {
            return "";
        }
    }

    /** "ip:port", "[v6]:port" or a bare address -> {host, port or null}. */
    public static String[] splitHostPort(String address) {
        if (address == null) return new String[]{null, null};
        String value = address.trim();

        if (value.startsWith("[")) {
            int close = value.indexOf(']');
            if (close < 0) return new String[]{value, null};
            String host = value.substring(1, close);
            String rest = value.substring(close + 1);
            return new String[]{host, rest.startsWith(":") ? rest.substring(1) : null};
        }

        int colon = value.indexOf(':');
        if (colon > 0 && value.indexOf(':', colon + 1) < 0)
            return new String[]{value.substring(0, colon), value.substring(colon + 1)};

        return new String[]{value, null};
    }
}
