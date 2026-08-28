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

import java.net.InetAddress;
import java.net.NetworkInterface;
import java.util.Collections;
import java.util.Enumeration;
import java.util.List;
import java.util.Locale;
import java.util.regex.Pattern;

import android.app.AlertDialog;
import android.app.Dialog;
import android.app.Service;
import android.content.Context;
import android.content.DialogInterface;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.SharedPreferences.Editor;
import android.content.pm.PackageManager;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.util.Log;
import android.view.View;
import android.view.WindowManager;
import android.view.inputmethod.InputMethodManager;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;
import android.graphics.Color;

import com.seleuco.mame4droid.MAME4droid;
import com.seleuco.mame4droid.Emulator;
import com.seleuco.mame4droid.helpers.PrefsHelper;
import com.seleuco.mame4droid.widgets.WarnWidget;
import com.seleuco.mame4droid.R;


public class NetPlayHelper {

    /** Shared-preference key for the rollback mode toggle. */
    public static final String PREF_NETPLAY_ROLLBACK_MODE = "netplay_rollback_mode";

    /* Local Network Protections (Android 17 / API 37): LAN UDP send AND
     * receive are blocked until ACCESS_LOCAL_NETWORK is granted; internet /
     * mobile play is exempt. requestCode routed back through
     * MAME4droid.onRequestPermissionsResult -> onLocalNetPermissionResult(). */
    public static final int REQ_LOCAL_NETWORK = 43;
    private Runnable pendingLocalNetAction = null;

    protected Dialog netplayDlg = null;

    /* Waiting/connecting dialog: custom view = [small spinner + status
     * line] over a scrollable body, so big system fonts can never push the
     * Share/Peer IP buttons off screen. */
    protected AlertDialog progressDialog = null;
    private TextView progressText = null;

    private View buildProgressView(String status, String initialBody) {
        float d = mm.getResources().getDisplayMetrics().density;
        LinearLayout root = new LinearLayout(mm);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding((int) (20 * d), (int) (14 * d), (int) (20 * d), 0);
        LinearLayout row = new LinearLayout(mm);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(android.view.Gravity.CENTER_VERTICAL);
        ProgressBar pb = new ProgressBar(mm, null, android.R.attr.progressBarStyleSmall);
        row.addView(pb, new LinearLayout.LayoutParams((int) (20 * d), (int) (20 * d)));
        TextView title = new TextView(mm);
        title.setText(status);
        title.setTypeface(null, android.graphics.Typeface.BOLD);
        title.setPadding((int) (10 * d), 0, 0, 0);
        row.addView(title);
        root.addView(row);
        ScrollView sc = new ScrollView(mm);
        progressText = new TextView(mm);
        progressText.setText(initialBody);
        progressText.setPadding(0, (int) (12 * d), 0, (int) (8 * d));
        sc.addView(progressText);
        root.addView(sc, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT));
        return root;
    }

    /* First clipboard item as trimmed text, or null if empty/unavailable. */
    private String readClipboard() {
        try {
            android.content.ClipboardManager cb = (android.content.ClipboardManager)
                    mm.getSystemService(Context.CLIPBOARD_SERVICE);
            if (cb == null || !cb.hasPrimaryClip()) return null;
            android.content.ClipData clip = cb.getPrimaryClip();
            if (clip == null || clip.getItemCount() == 0) return null;
            CharSequence t = clip.getItemAt(0).coerceToText(mm);
            return t == null ? null : t.toString().trim();
        } catch (Exception e) {
            return null;
        }
    }

    /* Per-address IPv6 enumeration logging (javac strips the dead branches
     * when false); the chosen join target still shows in the native log.
     * KEEP false FOR RELEASE. */
    private static final boolean V6_DEBUG = false;

    /* Cap on the client's JOIN retries. Only the handshake is timed: both
     * games are already up, so this measures one round trip, never a boot.
     * The host has its own JOIN_ACK_TIMEOUT_MS for the mirror case. */
    private static final long JOIN_ANSWER_TIMEOUT_MS = 30000;

    /* Typing an address by hand says nothing about the other side: they may
     * not have pressed Create yet, and cutting them off is worse than a long
     * wait. From the board we claimed a live room seconds ago, so 30s there
     * is already several times the worst punch. */
    private static final long JOIN_ANSWER_TIMEOUT_MANUAL_MS = 90000;

    private volatile boolean canceled = false;

    /* Host waiting-dialog text is composed by two racing workers (netplayInit
     * and the UPnP mapper): both funnel through postHostMessage().  While no
     * UPnP mapping exists the dialog shows the punch/forward hint instead. */
    private volatile String hostBaseMsg = null;
    private volatile String upnpLine = null;
    private volatile String upnpFallbackHint = "";

    /* Public board, when the user opted in: it automates the same tuple swap
     * the Share / Peer IP buttons do by hand, and both keep working whether
     * it publishes, fails or is switched off. */
    private volatile LobbySession lobby = null;
    private volatile String lobbyLine = null;
    private volatile LobbyClient.Endpoint lobbyPeer = null;
    private volatile long hostWaitMs = 0;
    private AlertDialog boardProgress = null;
    private volatile String lobbyClaimBase = null;
    private volatile String lobbyClaimRoom = null;
    private volatile String lobbyAimedAt = null;

    /* Kept from the moment a session connects until it ends, so the length of
     * the game can be reported: it is what separates a pairing that worked
     * from one that merely completed a handshake. */
    private volatile long sessionStartMs = 0;
    private volatile String playedGame = null;
    private volatile String playedRole = null;
    private volatile String playedPath = null;
    private volatile String playedPeerCountry = null;
    private volatile LobbyClient.Nat playedSelfNat = null;
    private volatile LobbyClient.Nat playedPeerNat = null;
    private volatile String joinPeerCountry = null;
    private volatile int joinMode = 0;
    private volatile int joinDelay = 0;
    private volatile boolean joinSameSite = false;
    private volatile boolean joinLocked = false;
    private volatile String joinRoom = null;
    private volatile boolean joinFromBoard = false;

    /* The room we claimed was a game already under way. */
    private volatile boolean joinIsDropIn = false;
    private volatile String joinGameName = null;
    private volatile LobbyClient.Nat joinPeerNat = null;
    /* Drop-in: the host plays on with the room published and whoever joins is
     * lifted into the running game. Off until the rollback screen offers it,
     * so every existing flow behaves exactly as before. */
    private volatile boolean dropIn = false;

    private volatile boolean lobbyPrivate = false;
    private volatile boolean samplerRunning = false;
    private volatile int publishRetrySeconds = 3;
    private volatile long publishWaitingSince = 0;
    private volatile boolean lobbyPaused = false;
    private volatile LobbyBoardDialog board = null;
    private volatile int playedRtt = 0;
    private volatile int playedJitter = 0;
    private volatile int playedRttMin = 0;
    private volatile int playedRttMax = 0;
    private volatile int playedMode = 0;
    private volatile int playedDelay = 0;
    private volatile boolean playedLocked = false;
    /* Snapshotted with the rest at connect time: which kind of session this
     * was is a property of the pairing, not of a setting the user may have
     * changed by the time it ends. */
    private volatile boolean playedDropIn = false;
    private volatile String playedRoom = null;

    /* Local address for the Share sheet; null when joining (the client only
     * ever shares its public tuple) or on mobile-only hosts. */
    private volatile String shareLocalAddr = null;

    /* Whether the Share sheet is the host's invite (true) or the client's
     * public-tuple reply (false): only the host adds local-address lines. */
    private volatile boolean sharingAsHost = false;

    /* Body of the connecting dialog on a LAN join: nothing to share or
     * exchange there, so it explains the situation instead. */
    private String lanConnectBody() {
        return mm.getString(R.string.np_lan_connect_body);
    }

    /** True when the user has selected ROLLBACK mode for the next session. */
    private boolean rollbackMode = false;

    protected MAME4droid mm = null;

    private static final Pattern IPV4_PATTERN =
            Pattern.compile(
                    "^(25[0-5]|2[0-4]\\d|[0-1]?\\d?\\d)(\\.(25[0-5]|2[0-4]\\d|[0-1]?\\d?\\d)){3}$");

    public NetPlayHelper(MAME4droid mm) {
        this.mm = mm;
    }

    /* High-performance Wi-Fi lock: Android's power-save duty-cycles the
     * radio on idle-looking traffic, and netplay's tiny UDP stream qualifies
     * -- that caused periodic RTT spikes big enough to saturate the rollback
     * window and stall both peers.  Held for the whole session; acquire/
     * release are idempotent (reference counting is off). */
    private WifiManager.WifiLock wifiLock = null;

    public void acquireWifiLock() {
        try {
            if (wifiLock == null) {
                WifiManager wifi = (WifiManager) mm.getApplicationContext()
                        .getSystemService(Context.WIFI_SERVICE);
                if (wifi == null) return;
                int mode = Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q
                        ? WifiManager.WIFI_MODE_FULL_LOW_LATENCY
                        : WifiManager.WIFI_MODE_FULL_HIGH_PERF;
                wifiLock = wifi.createWifiLock(mode, "MAME4droid:netplay");
                wifiLock.setReferenceCounted(false);
            }
            if (!wifiLock.isHeld()) {
                wifiLock.acquire();
                Log.d("MAME4droid_Netplay", "WifiLock acquired ("
                        + (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q
                           ? "FULL_LOW_LATENCY" : "FULL_HIGH_PERF") + ")");
            }
        } catch (Throwable e) {
            e.printStackTrace();
        }
    }

    public void releaseWifiLock() {
        try {
            if (wifiLock != null && wifiLock.isHeld()) {
                wifiLock.release();
                Log.d("MAME4droid_Netplay", "WifiLock released");
            }
        } catch (Throwable e) {
            e.printStackTrace();
        }
    }

    DialogInterface.OnCancelListener dialogCancelListener = new DialogInterface.OnCancelListener() {
        public void onCancel(DialogInterface dialog) {
            Emulator.resume();
        }
    };

    protected void prepareButtons() {

        final Button startButton = (Button) netplayDlg.findViewById(R.id.StartGameBtn);
        final Button joinButton = (Button) netplayDlg.findViewById(R.id.JoinPeerGameBtn);
        final Button publicRoomsButton = (Button) netplayDlg.findViewById(R.id.PublicRoomsBtn);
        final Button disconnectButton = (Button) netplayDlg.findViewById(R.id.DisconnectBtn);
        final Button resyncButton = (Button) netplayDlg.findViewById(R.id.ResyncBtn);

        /* Deliberately not gated on having a game selected, unlike Start: a
         * client joining from the board doesn't choose the game, the host
         * imposes it and MAME loads it on its own. */
        publicRoomsButton.setEnabled(Emulator.getValue(Emulator.NETPLAY_HAS_CONNECTION) != 1);

        if (Emulator.getValue(Emulator.NETPLAY_HAS_CONNECTION) == 1) {
            startButton.setEnabled(false);
            joinButton.setEnabled(false);
            disconnectButton.setEnabled(true);
            /* resync only makes sense on a LIVE ROLLBACK session (the
             * native value also covers the big-state fallback that silently
             * switches a session to lockstep). */
            resyncButton.setEnabled(Emulator.getValue(Emulator.NETPLAY_IN_ROLLBACK) == 1);
        } else {
            startButton.setEnabled(true);
            joinButton.setEnabled(true);
            disconnectButton.setEnabled(false);
            resyncButton.setEnabled(false);
            /* if the session died natively since the last UI interaction,
             * drop the radio lock and the router mapping now.             */
            releaseWifiLock();
            deleteUpnpMappingAsync();
            /* Same place catches a session the peer or a desync ended: it
             * only fires once, and only if one was actually running. */
            reportSessionEnded(endingOutcome());
        }

        String name = Emulator.getValueStr(Emulator.GAME_SELECTED);
        if (name != null && name.length() != 0) {
            startButton.setText(mm.getString(R.string.np_start_game_named, name));
        } else {
            startButton.setText(mm.getString(R.string.np_start_game));
            startButton.setEnabled(false);
        }
    }

    public void createDialog() {

        if (!Emulator.isEmulating())
            return;

        netplayDlg = new Dialog(mm);

        netplayDlg.setContentView(R.layout.netplayview);
        netplayDlg.setTitle(mm.getString(R.string.np_dialog_title));
        netplayDlg.setCancelable(true);
        netplayDlg.setOnCancelListener(dialogCancelListener);

        final Button startButton = (Button) netplayDlg.findViewById(R.id.StartGameBtn);
        startButton.setOnClickListener(createGameClick);

        final Button joinButton = (Button) netplayDlg.findViewById(R.id.JoinPeerGameBtn);
        joinButton.setOnClickListener(joinGameClick);

        publicRoomsButton = (Button) netplayDlg.findViewById(R.id.PublicRoomsBtn);
        publicRoomsButton.setOnClickListener(publicRoomsClick);

        final Button disconnectButton = (Button) netplayDlg.findViewById(R.id.DisconnectBtn);
        disconnectButton.setOnClickListener(disconnectGameClick);

        final Button resyncButton = (Button) netplayDlg.findViewById(R.id.ResyncBtn);
        resyncButton.setOnClickListener(resyncGameClick);

        prepareButtons();

        netplayDlg.show();
        wakeLobbyServer();
        /* Paint whatever the board last told us straight away, then go and
         * ask: coming back from the board is then instant and free. */
        repaintRoomsButton();
        showRoomsWaiting();
    }

    /**
     * Put the number of rooms on the button that opens the board.
     *
     * Every single room published from outside so far was somebody hosting;
     * not one person opened the board to look first. So two hosts can sit
     * three taps apart and never see each other. A number on the button is
     * the cheapest way to say "there is something in there".
     */
    private void showRoomsWaiting() {
        if (!LobbySession.isUsable(mm)) return;

        final String base = mm.getPrefsHelper().getNetplayLobbyUrl();
        final int proto = Emulator.netplayGetProtocolVersion();
        new Thread(new Runnable() {
            public void run() {
                LobbyClient.Board list = LobbyClient.list(base, proto, null);
                if (list.ok()) noteRoomsOnBoard(list.rooms.size());
            }
        }).start();
    }

    /**
     * How many rooms the board holds right now.
     *
     * The board itself calls this on every refresh while it is open, so
     * coming back from it repaints the button for free -- the one request is
     * the one made when this dialog opens, and only when it opens.
     */
    public void noteRoomsOnBoard(final int rooms) {
        roomsOnBoard = rooms;
        mm.runOnUiThread(new Runnable() {
            public void run() {
                repaintRoomsButton();
            }
        });
    }

    /** UI thread. Silent when the dialog is gone or the board was empty. */
    private void repaintRoomsButton() {
        Button button = publicRoomsButton;
        if (button == null || netplayDlg == null || !netplayDlg.isShowing()) return;

        String label = mm.getString(R.string.np_public_rooms);
        button.setText(roomsOnBoard > 0
                ? mm.getString(R.string.np_public_rooms_count, label, roomsOnBoard)
                : label);
    }

    private volatile int roomsOnBoard = 0;
    private Button publicRoomsButton = null;

    /**
     * Second chance for the wake ping, in case the one at app start was
     * throttled away or the server has dozed off since. Costs nothing when
     * it was already nudged recently.
     */
    private void wakeLobbyServer() {
        LobbySession.wakeServer(mm);
    }

    protected static boolean isIPv4Address(final String input) {
        return IPV4_PATTERN.matcher(input).matches();
    }

    /* The single most useful LAN IPv4 for the dialog and the Share text
     * (wlan wins over ethernet/tethering); null on mobile-only devices. */
    private String getMainLocalIPv4() {
        String first = null;
        try {
            List<NetworkInterface> interfaces = Collections.list(NetworkInterface.getNetworkInterfaces());
            for (NetworkInterface intf : interfaces) {
                String name = intf.getName().toLowerCase();
                if (name.contains("rmnet") || name.contains("ccmni") || name.contains("p2p") || name.contains("dummy")) continue;
                for (InetAddress addr : Collections.list(intf.getInetAddresses())) {
                    if (addr.isLoopbackAddress()) continue;
                    String s = addr.getHostAddress().toUpperCase(Locale.getDefault());
                    if (!isIPv4Address(s)) continue;
                    if (name.contains("wlan")) return s;
                    if (first == null) first = s;
                }
            }
        } catch (Exception ex) {
            ex.printStackTrace();
        }
        return first;
    }

    /* Every non-loopback IPv4 except carrier interfaces, so the host can share
     * ALL its LAN/hotspot addresses and the peer keeps the one on its own /24
     * (a hotspot host often has both a mobile and a reachable AP address). */
    java.util.List<String> getAllLocalIPv4() {
        java.util.List<String> out = new java.util.ArrayList<String>();
        try {
            for (NetworkInterface intf : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                String name = intf.getName().toLowerCase();
                if (name.contains("rmnet") || name.contains("ccmni") || name.contains("p2p") || name.contains("dummy")) continue;
                for (InetAddress addr : Collections.list(intf.getInetAddresses())) {
                    if (addr.isLoopbackAddress()) continue;
                    String s = addr.getHostAddress().toUpperCase(Locale.getDefault());
                    if (isIPv4Address(s) && !out.contains(s)) out.add(s);
                }
            }
        } catch (Exception ex) {
            ex.printStackTrace();
        }
        return out;
    }

    /* Android share sheet with the addresses ready to paste on the other
     * end -- nobody should ever transcribe an IP by hand. */
    private void shareAddresses(int port) {
        StringBuilder sb = new StringBuilder(mm.getString(R.string.np_share_header)).append('\n');
        /* Host (shareLocalAddr != null) advertises every LAN/hotspot address so
         * the peer can pick a reachable one; the client shares only its public. */
        if (shareLocalAddr != null)
            for (String loc : getAllLocalIPv4())
                sb.append(mm.getString(R.string.np_share_same_network, loc + ":" + port)).append('\n');
        String info = Emulator.netplayGetPublicAddr();
        if (info != null && info.length() > 0) {
            String[] parts = info.split("\\|");
            sb.append(mm.getString(R.string.np_share_internet, parts[0])).append('\n');
            /* Auto host carries a second (v4) public as "alt=": share both
             * so one invite serves v6, v4 and LAN peers alike. */
            for (String p : parts)
                if (p.startsWith("alt="))
                    sb.append(mm.getString(R.string.np_share_internet, p.substring(4))).append('\n');
        } else if (sharingAsHost && mm.getPrefsHelper().getNetplayIpProtocol() != 0) {
            /* STUN failed: a global v6 needs no NAT so it IS the public invite;
             * a ULA is same-network only.  Label each so the peer knows. */
            for (String v6a : getAllLocalIPv6())
                sb.append(mm.getString(isPrivateIPv6(v6a)
                                ? R.string.np_share_same_network : R.string.np_share_internet,
                        "[" + v6a + "]:" + port)).append('\n');
        }
        Intent i = new Intent(Intent.ACTION_SEND);
        i.setType("text/plain");
        i.putExtra(Intent.EXTRA_TEXT, sb.toString().trim());
        try {
            mm.startActivity(Intent.createChooser(i, mm.getString(R.string.np_share_title)));
        } catch (Exception e) {
            e.printStackTrace();
        }
    }

    /* Both ip[:port] candidates in pasted text: [private, public], each null if
     * absent.  Labels are ignored (classification is by IP value), so it works
     * whatever locale the shared invite was written in. */
    private String[] addressCandidates(String s) {
        String privFirst = null, privOnSubnet = null, pub = null;
        if (s != null) {
            java.util.regex.Matcher m = java.util.regex.Pattern.compile(
                    "(\\d{1,3}(?:\\.\\d{1,3}){3})(:\\d{1,5})?").matcher(s);
            while (m.find()) {
                String ipStr = m.group(1);
                if (!isIPv4Address(ipStr)) continue;
                if (isPrivateIPv4(ipStr)) {
                    if (privFirst == null) privFirst = m.group();
                    if (privOnSubnet == null && sameSubnet24(m.group())) privOnSubnet = m.group();
                } else if (pub == null) pub = m.group();
            }
        }
        /* Prefer a private on OUR /24 (reachable) when the host shared several. */
        return new String[]{ privOnSubnet != null ? privOnSubnet : privFirst, pub };
    }

    /* Strip an optional ":port" -> bare IPv4. */
    private static String ipOnly(String ipWithPort) {
        if (ipWithPort == null) return null;
        int c = ipWithPort.indexOf(':');
        return c > 0 ? ipWithPort.substring(0, c) : ipWithPort;
    }

    /* Whether a private ip[:port] shares our own /24 (very likely same LAN). */
    private boolean sameSubnet24(String privWithPort) {
        String own = getMainLocalIPv4();
        String priv = ipOnly(privWithPort);
        if (own == null || priv == null) return false;
        return own.substring(0, own.lastIndexOf('.') + 1)
                .equals(priv.substring(0, priv.lastIndexOf('.') + 1));
    }

    /* Pulls a usable ip[:port] out of pasted text.  IPv6 pref forces the v6
     * candidate; Auto prefers a GLOBAL v6 only when using it is free --
     * mobile-only (no cheaper path exists) or the Wi-Fi itself has v6; with
     * v6 only via cellular BEHIND Wi-Fi, v4 keeps the session off the meter.
     * A ULA falls through to the battle-tested v4 order (consumer APs often
     * drop ULA NDP -- field-tested) as last resort. */
    protected String extractAddress(String s) {
        if (s == null) return "";
        String[] c = addressCandidates(s);
        String priv = c[0], pub = c[1];
        String v6 = findIPv6Candidate(s);
        int proto = mm.getPrefsHelper().getNetplayIpProtocol();
        if (proto == 1 && v6 != null) return v6;
        if (proto == 2 && v6 != null) {
            if (priv != null && sameSubnet24(priv)) return priv;
            if (!isPrivateIPv6(splitHostPort(v6)[0]) && hasIPv6Route()
                    && (getMainLocalIPv4() == null || hasNonCarrierGlobalV6()))
                return v6;
        }
        if (priv != null && sameSubnet24(priv)) return priv;
        if (pub != null) return pub;
        if (priv != null) return priv;
        if (v6 != null) return v6;
        return s.trim();
    }

    /* Decide LAN vs internet for a pasted invite, then join.  With BOTH host
     * IPs, a STUN probe compares publics: equal = same site -> LAN, else
     * internet; probe empty (offline / blocked) -> /24 heuristic. */
    private void resolveAndJoin(final String pasted) {
        /* A v6 target works the same on LAN and internet (no NAT), so the
         * v4 same-site probe below would add nothing: join it as-is. */
        String chosen = extractAddress(pasted);
        if (isIPv6Address(splitHostPort(chosen)[0])) {
            joinGame(chosen);
            return;
        }
        String[] c = addressCandidates(pasted);
        final String priv = c[0], pub = c[1];
        if (priv != null && pub != null) {
            final String hostPubIp = ipOnly(pub);
            new Thread(new Runnable() { public void run() {
                String myPub = Emulator.netplayProbePublicIp();
                final String chosen = (myPub != null && myPub.length() > 0)
                        ? (myPub.equals(hostPubIp) ? priv : pub)  /* same public IP -> same site -> LAN */
                        : extractAddress(pasted);                 /* probe failed -> /24 heuristic      */
                mm.runOnUiThread(new Runnable() { public void run() { joinGame(chosen); } });
            } }).start();
        } else {
            joinGame(extractAddress(pasted));
        }
    }

    /* Any non-loopback IPv4 at all (mobile data included): getMainLocalIPv4()
     * hides carrier interfaces on purpose, so a 4G-only host has no LAN
     * address yet still has internet -- it must not be treated as offline. */
    private boolean hasAnyIPv4() {
        try {
            List<NetworkInterface> interfaces = Collections.list(NetworkInterface.getNetworkInterfaces());
            for (NetworkInterface intf : interfaces) {
                List<InetAddress> addrs = Collections.list(intf.getInetAddresses());
                for (InetAddress addr : addrs) {
                    if (!addr.isLoopbackAddress()
                            && isIPv4Address(addr.getHostAddress().toUpperCase(Locale.getDefault())))
                        return true;
                }
            }
        } catch (Exception ex) {
        }
        return false;
    }

    /**
     * Everything that is decided per hosted game, in one place: the sync mode
     * and whether the room goes on the public board behind a PIN. Both are
     * choices about the game being created, so they belong here rather than
     * among the actions in the netplay dialog.
     */
    private void pickModeAndRun(final Runnable action) {
        // Read persisted mode
        SharedPreferences sp = mm.getPrefsHelper().getSharedPreferences();
        rollbackMode = sp.getBoolean(PREF_NETPLAY_ROLLBACK_MODE, false);

        float d = mm.getResources().getDisplayMetrics().density;
        LinearLayout box = new LinearLayout(mm);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setPadding((int) (20 * d), (int) (12 * d), (int) (20 * d), 0);

        final android.widget.RadioGroup modes = new android.widget.RadioGroup(mm);
        final android.widget.RadioButton lockstep = new android.widget.RadioButton(mm);
        lockstep.setId(1);
        lockstep.setText(mm.getString(R.string.np_mode_lockstep));
        lockstep.setPadding(0, (int) (4 * d), 0, (int) (4 * d));
        final android.widget.RadioButton rollback = new android.widget.RadioButton(mm);
        rollback.setId(2);
        rollback.setText(mm.getString(R.string.np_mode_rollback));
        rollback.setPadding(0, (int) (4 * d), 0, (int) (4 * d));
        modes.addView(lockstep);
        modes.addView(rollback);
        modes.check(rollbackMode ? 2 : 1);
        box.addView(modes);

        /* Only offered when a PIN exists: a box that cannot close the room
         * would promise privacy the room does not have. */
        final boolean hasPin = mm.getPrefsHelper().hasNetplayLobbyPin();
        final android.widget.CheckBox privateRoom = new android.widget.CheckBox(mm);
        privateRoom.setText(mm.getString(hasPin
                ? R.string.np_private_room : R.string.np_private_room_no_pin));
        privateRoom.setEnabled(hasPin);
        privateRoom.setChecked(hasPin && mm.getPrefsHelper().isNetplayLobbyPrivate());
        /* Set apart from the modes above: that pair is one choice, this is a
         * separate one, and with equal spacing they read as three options. */
        privateRoom.setPadding(0, (int) (14 * d), 0, (int) (4 * d));
        if (LobbySession.isUsable(mm)) box.addView(privateRoom);

        /* Drop-in. Rollback only, and not because of a UI preference: the
         * joiner is lifted into a running game with a state transfer, and
         * that machinery only exists in the rollback path. The box follows
         * the radio buttons live so it cannot be left ticked under a mode
         * that could not honour it. */
        /* Ticking the box on a game that did not boot pinned costs the run
         * in progress. Said here, next to the box, while it can still be
         * unticked -- not as a surprise once the machine resets. */
        final boolean wouldRestart = Emulator.isInGame()
                && Emulator.getValue(Emulator.NETPLAY_KEEPS_GAME) == 0;
        final TextView dropInRestart = new TextView(mm);
        dropInRestart.setText(mm.getString(R.string.np_drop_in_restarts));
        dropInRestart.setTextSize(android.util.TypedValue.COMPLEX_UNIT_SP, 12);
        dropInRestart.setTextColor(android.graphics.Color.YELLOW);
        dropInRestart.setPadding(0, 0, 0, (int) (4 * d));
        dropInRestart.setVisibility(View.GONE);

        final android.widget.CheckBox dropInBox = new android.widget.CheckBox(mm);
        dropInBox.setText(mm.getString(R.string.np_drop_in));
        dropInBox.setPadding(0, (int) (14 * d), 0, 0);
        final TextView dropInWhy = new TextView(mm);
        dropInWhy.setText(mm.getString(R.string.np_drop_in_summary));
        dropInWhy.setTextSize(android.util.TypedValue.COMPLEX_UNIT_SP, 12);
        dropInWhy.setPadding(0, 0, 0, (int) (4 * d));
        if (LobbySession.isUsable(mm)) {
            box.addView(dropInBox);
            box.addView(dropInWhy);
            box.addView(dropInRestart);
            dropInBox.setOnCheckedChangeListener(
                    new android.widget.CompoundButton.OnCheckedChangeListener() {
                        public void onCheckedChanged(android.widget.CompoundButton b, boolean on) {
                            dropInRestart.setVisibility(on && wouldRestart
                                    ? View.VISIBLE : View.GONE);
                        }
                    });
            /* Whatever was chosen last time. Not a default we picked: a host
             * who shares addresses by hand needs the waiting dialog this hides,
             * so nobody gets drop-in without having asked for it once. Set
             * after the listener so the restart warning appears with it. */
            dropInBox.setChecked(mm.getPrefsHelper().isNetplayDropIn());
            dropInBox.setEnabled(rollbackMode);
            dropInWhy.setEnabled(rollbackMode);
            modes.setOnCheckedChangeListener(
                    new android.widget.RadioGroup.OnCheckedChangeListener() {
                        public void onCheckedChanged(android.widget.RadioGroup g, int id) {
                            /* Greyed out under lockstep, never unticked: coming
                             * back to rollback should find the choice where it
                             * was left, and starting is already gated on the
                             * mode, so a ticked-but-disabled box does nothing. */
                            boolean roll = (id == 2);
                            dropInBox.setEnabled(roll);
                            dropInWhy.setEnabled(roll);
                            dropInRestart.setVisibility(roll && dropInBox.isChecked()
                                    && wouldRestart ? View.VISIBLE : View.GONE);
                        }
                    });
        }

        new AlertDialog.Builder(mm)
            .setTitle(mm.getString(R.string.np_create_options_title))
            .setView(box)
            .setPositiveButton(mm.getString(R.string.ok), new DialogInterface.OnClickListener() {
                public void onClick(DialogInterface dialog, int which) {
                    rollbackMode = (modes.getCheckedRadioButtonId() == 2);
                    // Persist choice
                    SharedPreferences sp = mm.getPrefsHelper().getSharedPreferences();
                    sp.edit().putBoolean(PREF_NETPLAY_ROLLBACK_MODE, rollbackMode).apply();
                    mm.getPrefsHelper().setNetplayLobbyPrivate(hasPin && privateRoom.isChecked());
                    /* Remember the tick, not the outcome: under lockstep the box
                     * is disabled and dropIn ends up false, and forgetting the
                     * choice for that reason would lose it on every mode switch. */
                    mm.getPrefsHelper().setNetplayDropIn(dropInBox.isChecked());
                    dropIn = rollbackMode && dropInBox.isChecked() && LobbySession.isUsable(mm);
                    // Apply mode to native layer BEFORE netplayInit()
                    Emulator.netplaySetMode(rollbackMode ? 1 : 0);
                    action.run();
                }
            })
            .setNegativeButton(mm.getString(R.string.cancel), null)
            .show();
    }

    /* Ensures the ACCESS_LOCAL_NETWORK runtime permission before a LAN
     * session (Android 17+). Returns true if the caller may run {@code action}
     * inline right now; false means the system dialog was launched and
     * {@code action} runs exactly once from onLocalNetPermissionResult() after
     * the user answers (no re-gating, so a denial can't loop). Below API 37 the
     * permission doesn't exist and LAN is granted implicitly, so we proceed. */
    boolean ensureLocalNet(Runnable action) {
        if (Build.VERSION.SDK_INT < 37)
            return true;
        if (mm.checkSelfPermission("android.permission.ACCESS_LOCAL_NETWORK")
                == PackageManager.PERMISSION_GRANTED)
            return true;
        pendingLocalNetAction = action;
        mm.requestPermissions(
                new String[]{"android.permission.ACCESS_LOCAL_NETWORK"}, REQ_LOCAL_NETWORK);
        return false;
    }

    /* Callback from MAME4droid.onRequestPermissionsResult. We proceed no
     * matter the verdict: a denial only costs LAN (internet play never needed
     * the permission), so the user continues and the LAN path fails the usual
     * way rather than hard-blocking the whole netplay dialog. */
    public void onLocalNetPermissionResult() {
        Runnable a = pendingLocalNetAction;
        pendingLocalNetAction = null;
        if (a != null) a.run();
    }

    /* Hosting goes straight to the waiting dialog: the punch target (hole
     * punching) is only ever armed later via its Peer IP button, and only
     * needed when the host isn't directly reachable (no UPnP/forward). */
    Button.OnClickListener createGameClick = new Button.OnClickListener() {
        public void onClick(View v) {
            Runnable action = new Runnable() { public void run() {
                pickModeAndRun(new Runnable() { public void run() { createGame(); } });
            } };
            if (ensureLocalNet(action)) action.run();
        }
    };

    /* Hot punch-target prompt while the host waits: feeds
     * netplaySetPunchAddr, which the network thread applies within ~500ms. */
    protected void promptHotPunchAddr(final int gamePort) {
        AlertDialog.Builder alert = new AlertDialog.Builder(mm);
        alert.setTitle(mm.getString(R.string.np_peer_ip_title));

        final EditText input = new EditText(mm);
        String punch = mm.getPrefsHelper().getSharedPreferences().getString(PrefsHelper.PREF_NETPLAY_PUNCHADDR, "");
        input.setText(punch);
        input.setSelection(input.getText().length());

        /* Custom view: hint + field + a Clear/Paste row (same as the join dialog). */
        float d = mm.getResources().getDisplayMetrics().density;
        LinearLayout box = new LinearLayout(mm);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setPadding((int) (20 * d), (int) (8 * d), (int) (20 * d), 0);
        TextView hint = new TextView(mm);
        hint.setText(mm.getString(R.string.np_peer_ip_hint));
        hint.setPadding(0, 0, 0, (int) (8 * d));
        box.addView(hint);
        box.addView(input);
        LinearLayout btnRow = new LinearLayout(mm);
        btnRow.setOrientation(LinearLayout.HORIZONTAL);
        btnRow.setGravity(android.view.Gravity.END);
        Button clearBtn = new Button(mm);
        clearBtn.setText(mm.getString(R.string.clear));
        clearBtn.setOnClickListener(new View.OnClickListener() {
            public void onClick(View b) { input.setText(""); }
        });
        Button pasteBtn = new Button(mm);
        pasteBtn.setText(mm.getString(R.string.paste));
        pasteBtn.setOnClickListener(new View.OnClickListener() {
            public void onClick(View b) {
                String clip = readClipboard();
                if (clip != null && clip.length() > 0) {
                    input.setText(clip);
                    input.setSelection(input.getText().length());
                }
            }
        });
        btnRow.addView(clearBtn);
        btnRow.addView(pasteBtn);
        box.addView(btnRow);
        alert.setView(box);

        alert.setPositiveButton(mm.getString(R.string.ok), new DialogInterface.OnClickListener() {
            public void onClick(DialogInterface dialog, int whichButton) {
                /* Tolerates a pasted Share message (labels included). */
                String s = extractAddress(input.getText().toString());
                String host = null;
                int p = gamePort;
                if (s.length() > 0) {
                    String[] hp = splitHostPort(s);
                    host = hp[0];
                    if (hp[1] != null) { try { p = Integer.parseInt(hp[1]); } catch (Exception e) {} }
                    /* Punch target must be a literal IP (the network thread's
                     * hot resolve is numeric-only) of a family this socket can
                     * send to; reject bad input instead of punching nowhere. */
                    boolean v4 = isIPv4Address(host), v6 = isIPv6Address(host);
                    int proto = mm.getPrefsHelper().getNetplayIpProtocol();
                    if (!v4 && !v6) {
                        showNetplayError(mm.getString(R.string.np_invalid_ip));
                        return;
                    }
                    if ((proto == 1 && v4) || (proto == 0 && v6)) {
                        showNetplayError(mm.getString(R.string.np_ip_family_mismatch,
                                v4 ? "IPv4" : "IPv6", proto == 1 ? "IPv6" : "IPv4"));
                        return;
                    }
                }
                mm.getPrefsHelper().getSharedPreferences().edit()
                        .putString(PrefsHelper.PREF_NETPLAY_PUNCHADDR, s).commit();
                Emulator.netplaySetPunchAddr(host, p);
            }
        });
        alert.setNegativeButton(mm.getString(R.string.cancel), null);
        alert.show();
    }

    /* "host[:port]" -> {host, portStr|null}.  "[v6]:port" unwraps its
     * brackets; a bare IPv6 (several ':') is taken whole as host. */
    protected static String[] splitHostPort(String s) {
        if (s.startsWith("[")) {
            int e = s.indexOf(']');
            if (e > 1) {
                String port = (e + 2 < s.length() && s.charAt(e + 1) == ':')
                        ? s.substring(e + 2) : null;
                return new String[]{s.substring(1, e), port};
            }
        }
        int i = s.lastIndexOf(':');
        if (i > 0 && i < s.length() - 1 && s.indexOf(':') == i)
            return new String[]{s.substring(0, i), s.substring(i + 1)};
        return new String[]{s, null};
    }

    /* Private/CGNAT/link-local ranges never need STUN; anything else
     * (public IP or hostname) flips the join flow into internet mode. */
    protected static boolean isPrivateIPv4(String ip) {
        if (!isIPv4Address(ip)) return false;
        try {
            String[] p = ip.split("\\.");
            int a = Integer.parseInt(p[0]), b = Integer.parseInt(p[1]);
            if (a == 10 || a == 127) return true;
            if (a == 192 && b == 168) return true;
            if (a == 172 && b >= 16 && b <= 31) return true;
            if (a == 169 && b == 254) return true;
            if (a == 100 && b >= 64 && b <= 127) return true;
        } catch (Exception e) {
        }
        return false;
    }

    /* Plausible DNS hostname (dyndns etc.): letters/digits/dots/hyphens
     * with at least one letter, so garbage and malformed IPs don't reach
     * the socket as a bogus resolve. */
    protected static boolean looksLikeHostname(String h) {
        return h != null && h.matches("[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?")
                && h.matches(".*[A-Za-z].*");
    }

    /* Numeric IPv6 literal (no brackets, no scope id). */
    protected static boolean isIPv6Address(String ip) {
        if (ip == null || ip.indexOf(':') < 0) return false;
        try {
            return android.net.InetAddresses.isNumericAddress(ip);
        } catch (Throwable t) {
            return ip.indexOf(':') != ip.lastIndexOf(':');
        }
    }

    /* Loopback/link-local/ULA: only reachable inside the site, so they
     * never flip the join flow into internet mode (isPrivateIPv4's twin). */
    protected static boolean isPrivateIPv6(String ip) {
        String t = ip.toLowerCase(Locale.US);
        return t.equals("::1") || t.startsWith("fe8") || t.startsWith("fe9")
                || t.startsWith("fea") || t.startsWith("feb")
                || t.startsWith("fc") || t.startsWith("fd");
    }

    /* First IPv6 in pasted text, in join form: "[v6]:port" when bracketed
     * with a port, bare "v6" otherwise.  Null when the text has none. */
    protected static String findIPv6Candidate(String s) {
        if (s == null) return null;
        java.util.regex.Matcher m = java.util.regex.Pattern
                .compile("\\[([0-9A-Fa-f:.]+)\\](:\\d{1,5})?").matcher(s);
        while (m.find())
            if (isIPv6Address(m.group(1))) return m.group();
        for (String tok : s.split("[^0-9A-Fa-f:.]+")) {
            int c = tok.indexOf(':');
            if (c >= 0 && tok.indexOf(':', c + 1) >= 0 && isIPv6Address(tok))
                return tok;
        }
        return null;
    }

    /* Usable IPv6 addresses, globals first then ULA, scope stripped.  A global
     * (2000::/3) reaches the internet with no NAT; a ULA (fc/fd) reaches only
     * same-LAN peers -- still valid when the ISP gives no v6 prefix.  Only
     * LAN-like interfaces count (allowlist below); link-local/loopback are
     * dropped.  NetworkInterface needs no ACCESS_NETWORK_STATE permission. */
    private java.util.List<String> getAllLocalIPv6() {
        java.util.List<String> globals = new java.util.ArrayList<String>();
        java.util.List<String> ulas = new java.util.ArrayList<String>();
        try {
            if (V6_DEBUG) Log.d("MAME4droid_Netplay", "v6enum: scanning interfaces...");
            for (NetworkInterface intf : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                String name = intf.getName().toLowerCase();
                /* ALLOWLIST (fails closed): only interfaces a peer could really
                 * reach us on -- Wi-Fi/ethernet, hotspot/tether, VPN (Tailscale).
                 * Everything unknown (rmnet/ccmni/seth cellular, ipsec VoWiFi,
                 * clat, p2p, dummy, vendor exotics) never makes an invite. */
                boolean lanLike = name.startsWith("wlan") || name.startsWith("eth")
                        || name.startsWith("ap") || name.startsWith("softap")
                        || name.startsWith("swlan") || name.startsWith("usb")
                        || name.startsWith("rndis") || name.startsWith("ncm")
                        || name.startsWith("bt-pan") || name.startsWith("tun")
                        || name.startsWith("utun") || name.startsWith("tap")
                        || name.startsWith("wg");
                boolean carrier = !lanLike;
                for (InetAddress addr : Collections.list(intf.getInetAddresses())) {
                    if (!(addr instanceof java.net.Inet6Address)) continue;
                    String s = addr.getHostAddress();
                    int pc = s.indexOf('%');
                    if (pc > 0) s = s.substring(0, pc);
                    byte[] b = addr.getAddress();
                    boolean global = (b[0] & 0xE0) == 0x20;   /* 2000::/3 */
                    boolean ula    = (b[0] & 0xFE) == 0xFC;   /* fc00::/7 */
                    if (V6_DEBUG) {
                        String kind = addr.isLoopbackAddress() ? "loopback"
                                : addr.isLinkLocalAddress() ? "link-local"
                                : global ? "global" : ula ? "ULA" : "other";
                        Log.d("MAME4droid_Netplay", "v6enum: " + name + (carrier ? " (excluded)" : "")
                                + " " + s + " [" + kind + "]");
                    }
                    if (carrier || addr.isLoopbackAddress() || addr.isLinkLocalAddress()
                            || (!global && !ula)) continue;
                    java.util.List<String> tgt = global ? globals : ulas;
                    if (!tgt.contains(s)) tgt.add(s);
                }
            }
            if (V6_DEBUG) Log.d("MAME4droid_Netplay", "v6enum: result globals=" + globals.size() + " ulas=" + ulas.size());
        } catch (Exception ex) {
            ex.printStackTrace();
        }
        /* One address per kind is enough for an invite (SLAAC gives several);
         * globals (internet-capable) first, then the LAN-only ULA. */
        java.util.List<String> out = new java.util.ArrayList<String>();
        if (!globals.isEmpty()) out.add(pickStableV6(globals));
        if (!ulas.isEmpty()) out.add(pickStableV6(ulas));
        return out;
    }

    /* Prefer the stable EUI-64 address ("ff:fe" infix) over rotating
     * privacy ones: it survives longer, so the invite stays valid. */
    private static String pickStableV6(java.util.List<String> l) {
        for (String a : l)
            if (a.contains("ff:fe")) return a;
        return l.get(0);
    }

    /* A global v6 on a NON-carrier interface (Wi-Fi/ethernet): v6 there is
     * as free as v4.  False when the only v6 is cellular -- usable, but it
     * bills mobile data even while Wi-Fi is connected (Pixel-style OSes
     * keep the cell v6 route alive behind Wi-Fi). */
    private boolean hasNonCarrierGlobalV6() {
        for (String a : getAllLocalIPv6())
            if (!isPrivateIPv6(a)) return true;
        return false;
    }

    /* Can the ACTIVE network reach global v6?  Java twin of the native
     * skt_have_ipv6_route(): a UDP connect() sends nothing but asks the
     * kernel routing table, so it is interface-agnostic (rmnet included).
     * Run off-thread (StrictMode counts connect() as network I/O). */
    private boolean hasIPv6Route() {
        final boolean[] ok = {false};
        Thread t = new Thread(new Runnable() {
            public void run() {
                java.net.DatagramSocket ds = null;
                try {
                    ds = new java.net.DatagramSocket();
                    ds.connect(new java.net.InetSocketAddress(
                            InetAddress.getByName("2001:4860:4860::8888"), 53));
                    ok[0] = true;
                } catch (Exception e) {
                } finally {
                    if (ds != null) ds.close();
                }
            }
        });
        t.start();
        try { t.join(500); } catch (InterruptedException e) {}
        return ok[0];
    }

    /* Any global/ULA v6 on ANY interface, mobile (rmnet) INCLUDED -- decides
     * whether strict-v6 play is possible at all.  Unlike getAllLocalIPv6, which
     * skips carrier interfaces (their v6 makes dead LAN invites), STUN can still
     * publish an rmnet global, so the guard must not refuse it.  The ipsec
     * (VoWiFi/IMS) tunnel is app-unusable: never counts. */
    boolean hasUsableIPv6() {
        try {
            for (NetworkInterface intf : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                if (intf.getName().toLowerCase().contains("ipsec")) continue;
                for (InetAddress addr : Collections.list(intf.getInetAddresses())) {
                    if (!(addr instanceof java.net.Inet6Address)
                            || addr.isLoopbackAddress() || addr.isLinkLocalAddress()) continue;
                    byte[] b = addr.getAddress();
                    if ((b[0] & 0xE0) == 0x20 || (b[0] & 0xFE) == 0xFC) return true;
                }
            }
        } catch (Exception ex) {
            ex.printStackTrace();
        }
        return false;
    }

    /* An error the user MUST see while the NetPlay menu is open: a WarnWidget
     * draws on the activity frame, BEHIND dialogs, so it would be hidden.  An
     * AlertDialog has its own window and sits on top.  UI thread only. */
    void showNetplayError(String msg) {
        new AlertDialog.Builder(mm)
                .setMessage(msg)
                .setPositiveButton(android.R.string.ok, null)
                .show();
    }

    /* UPnP SOAP calls are network I/O: never on the UI thread. */
    public void deleteUpnpMappingAsync() {
        if (!UpnpHelper.isMapped()) return;
        new Thread(new Runnable() {
            public void run() {
                UpnpHelper.deletePortMapping();
            }
        }).start();
    }

    /* Clean native exit arrives via the warn channel with the connection
     * flag already 0.  Pre-join TOASTs (e.g. a build-mismatch reject) also
     * read 0 while the host keeps waiting: the dialog check keeps those
     * from unmapping the port mid-wait. */
    public void onNetplaySessionGone() {
        if (progressDialog != null && progressDialog.isShowing()) return;
        deleteUpnpMappingAsync();
    }

    /**
     * "\nCN flag CN . IPv6", or "" when neither half is known. Built from
     * tokens only, so it reads the same in every language the app ships in,
     * and it tells nobody anything the board did not already show publicly.
     */
    private String peerOriginSuffix(String country, String path) {
        /* A flag is only interesting when the other player is somewhere else.
         * On your own network you already know where they are, and telling
         * somebody their flatmate is in Spain reads as a bug. */
        boolean local = "lan".equals(path);

        String where = "";
        if (!local && country != null && country.length() == 2) {
            String flag = LobbySession.flagOf(country);
            where = flag.length() == 0 ? country : flag + " " + country;
        }
        String how = (path != null) ? LobbySession.pathLabel(path) : "";

        if (where.length() == 0 && how.length() == 0) return "";
        if (where.length() == 0) return "\n" + how;
        if (how.length() == 0) return "\n" + where;
        return "\n" + where + " · " + how;
    }

    /**
     * Second paragraph for the "peer disconnected" notice, host side only.
     *
     * A drop-in host whose guest leaves is left playing alone with the room
     * gone, and nothing on screen says the game survived or that putting it
     * back on the board costs nothing. Both are worth saying, once.
     *
     * @return the extra line, or "" when it does not apply.
     */
    public String dropInAgainHint() {
        if (!playedDropIn || !"host".equals(playedRole)) return "";
        /* Blank line, not a line break: it is a separate thought, and it is
         * the one the reader has to act on. */
        return "\n\n" + mm.getString(R.string.np_drop_in_again);
    }

    /** Repaint the host waiting dialog from hostBaseMsg + upnpLine. */
    private void postHostMessage() {
        final String base = hostBaseMsg;
        if (base == null) return;
        /* extra starts with "\n"; the added "\n" makes the blank line
         * between the IP block and the UPnP/fallback status. */
        final String extra = (upnpLine != null) ? upnpLine : upnpFallbackHint;
        String body = extra.isEmpty() ? base : base + "\n" + extra;
        /* Board status goes last: it is the newest information and the one
         * that changes while the dialog is up. The room's own code rides with
         * it so the host can read it out: "join the one called K7M2QP4A" is
         * the only way to tell two rooms of the same game apart. */
        final String board = lobbyLine;
        if (board != null) {
            body += "\n" + board;
            LobbySession live = lobby;
            String code = (live != null) ? live.getRoomId() : null;
            if (code != null && code.length() > 0)
                body += "\n" + mm.getString(R.string.np_lobby_room_code, code);
        }
        final String msg = body;
        mm.runOnUiThread(new Runnable() {
            public void run() {
                if (progressDialog != null && progressDialog.isShowing() && progressText != null)
                    progressText.setText(msg);
            }
        });
    }

    /**
     * Asked once, before anything of ours reaches the board. Declining turns
     * the feature off instead of asking again on every game, and the setting
     * is there to change one's mind.
     *
     * @return true when the caller may carry on right now; false means the
     * dialog is up and {@code onAccept} runs from it.
     */
    boolean ensureLobbyConsent(final Runnable onAccept, final Runnable onDecline) {
        final PrefsHelper prefs = mm.getPrefsHelper();
        if (!prefs.isNetplayLobbyEnabled() || prefs.isNetplayLobbyConsentGiven())
            return true;

        new AlertDialog.Builder(mm)
                .setTitle(mm.getString(R.string.np_lobby_consent_title))
                .setMessage(mm.getString(R.string.np_lobby_consent_body))
                .setCancelable(false)
                .setPositiveButton(mm.getString(R.string.np_lobby_consent_accept),
                        new DialogInterface.OnClickListener() {
                            public void onClick(DialogInterface d, int w) {
                                prefs.setNetplayLobbyConsentGiven(true);
                                if (onAccept != null) onAccept.run();
                            }
                        })
                .setNegativeButton(mm.getString(R.string.np_lobby_consent_decline),
                        new DialogInterface.OnClickListener() {
                            public void onClick(DialogInterface d, int w) {
                                prefs.setNetplayLobbyEnabled(false);
                                if (onDecline != null) onDecline.run();
                            }
                        })
                .show();
        return false;
    }

    private void openBoard() {
        board = new LobbyBoardDialog(mm, this);
        board.show();
    }

    /**
     * App going to the background: stop talking to the board. A list nobody is
     * looking at spends the server's traffic and the phone's battery, and
     * Android freezes those threads sooner or later anyway. The host's room
     * expires within the minute, which the republish path treats as normal.
     */
    public void pause() {
        lobbyPaused = true;

        LobbyBoardDialog open = board;
        if (open != null) open.pause();
    }

    /** Back in the foreground: pick the board up where it left off. */
    public void resume() {
        lobbyPaused = false;

        LobbyBoardDialog open = board;
        if (open != null) open.resume();
    }

    /** Open the board of games other people are hosting. */
    public void showPublicRooms() {
        if (Emulator.netplayGetProtocolVersion() <= 0) {
            showNetplayError(mm.getString(R.string.np_lobby_unsupported));
            return;
        }
        if (!mm.getPrefsHelper().isNetplayLobbyEnabled()) {
            showNetplayError(mm.getString(R.string.np_lobby_switched_off));
            return;
        }
        /* Any room on the board may turn out to be on this very network, and
         * Android 17 blocks local traffic without the permission -- silently,
         * so the join would just sit at "connecting" forever. Ask here, like
         * both manual paths do, since the address is only known later. */
        final Runnable open = new Runnable() {
            public void run() {
                if (ensureLocalNet(new Runnable() {
                    public void run() {
                        openBoard();
                    }
                })) openBoard();
            }
        };

        /* Declining consent means the board never opens: it was the whole
         * point of the tap, so there is nothing to fall back to. */
        if (!ensureLobbyConsent(open, null)) return;
        open.run();
    }

    /** The netplay port from the settings, which is also our bind port. */
    int getConfiguredPort() {
        try {
            return Integer.parseInt(mm.getPrefsHelper().getNetplayPort());
        } catch (Exception e) {
            return 2080;
        }
    }

    /** Our private v4 addresses, for the LAN half of a rendezvous. */
    java.util.List<String> getLocalAddresses() {
        return getAllLocalIPv4();
    }

    /** Spinner while the board claims a room; the join dialog takes over. */
    void showBoardProgress(String message) {
        canceled = false;
        AlertDialog.Builder builder = new AlertDialog.Builder(mm);
        builder.setView(buildProgressView(message, ""));
        builder.setCancelable(false);
        boardProgress = builder.create();
        boardProgress.show();
    }

    void hideBoardProgress() {
        if (boardProgress != null && boardProgress.isShowing())
            boardProgress.dismiss();
        boardProgress = null;
    }

    /* ---- Public board ------------------------------------------------- *
     * Automates the tuple swap the Share / Peer IP buttons do by hand.  Every
     * step is best-effort: a board that is off, unreachable or refuses us just
     * leaves the manual path exactly as it was.  Worker thread only.        */

    /** Advertise the running game, once STUN has produced our public tuple. */
    private void publishOnLobby(int gamePort) {
        if (!LobbySession.isUsable(mm)) return;

        String game = Emulator.getValueStr(Emulator.GAME_SELECTED);
        if (game == null || game.length() == 0) return;

        java.util.List<String> lan = new java.util.ArrayList<String>();
        for (String local : getAllLocalIPv4())
            lan.add(local + ":" + gamePort);

        LobbySession.rememberNat(mm, Emulator.netplayGetPublicAddr(), UpnpHelper.isMapped());

        /* Private only when the user asked for it AND a usable PIN exists:
         * publishing without one would put a room the host believes is closed
         * in front of everybody. */
        boolean wantsPrivate = mm.getPrefsHelper().isNetplayLobbyPrivate()
                && mm.getPrefsHelper().hasNetplayLobbyPin();
        String pin = wantsPrivate ? mm.getPrefsHelper().getNetplayLobbyPin() : null;

        if (publishWaitingSince == 0)
            publishWaitingSince = android.os.SystemClock.elapsedRealtime();

        LobbySession board = new LobbySession(mm);
        boolean published = board.publish(game, rollbackMode ? 1 : 0,
                mm.getPrefsHelper().getNetplayDelayValue(),
                mm.getPrefsHelper().isNetplayAllowPluginsEnabled(),
                lan, UpnpHelper.isMapped(), pin, dropIn);

        lobby = published ? board : null;
        lobbyPrivate = published && pin != null;

        /* How soon to try again, decided by who said no. A call that never
         * reached the lobby means the free instance is still waking, and those
         * cost nothing -- the platform answers them, so our own rate limit
         * never sees them. A refusal that DID come from our server is either
         * temporary and expensive to retry (429) or permanent (a rejected
         * address), and hammering it would only spend the create budget. */
        if (published) {
            publishRetrySeconds = 0;
        } else if (board.lastPublishWasUnreachable()) {
            publishRetrySeconds = 3;
        } else if (board.getLastPublishStatus() == 429) {
            publishRetrySeconds = 60;
        } else {
            publishRetrySeconds = 0;
        }

        /* Saying "unavailable" while a retry is already scheduled reads as
         * giving up, and on a cold start that is exactly the moment it is
         * least true. Same rule as the board: hopeful for a minute. But the
         * room quota arrives as a 429 too and is none of those things -- it
         * is a full board from this address, and only saying so lets anyone
         * work out that a friend has to finish first. */
        int line;
        if (published) {
            line = lobbyPrivate ? R.string.np_lobby_published_private
                    : R.string.np_lobby_published;
        } else if (board.lastPublishHitRoomQuota()) {
            line = R.string.np_lobby_too_many;
        } else if (publishRetrySeconds > 0) {
            long waited = android.os.SystemClock.elapsedRealtime() - publishWaitingSince;
            line = (waited < 60000L) ? R.string.np_lobby_waking
                    : R.string.np_lobby_unavailable;
        } else {
            line = R.string.np_lobby_unavailable;
        }

        lobbyLine = mm.getString(line);
        postHostMessage();
    }


    /* Drop-in has gone live: the room is up and the game is the user's again. */
    private volatile boolean dropInLive = false;

    /**
     * Close the waiting dialogs and let the host play. Nothing about the
     * session is torn down -- the worker thread stays in its loop keeping the
     * room alive, and the native side simply has not begun a netplay session
     * yet, so the machine runs the way it does with netplay switched off.
     */
    private void goLiveForDropIn() {
        dropInLive = true;
        final String code = (lobby != null) ? lobby.getRoomId() : "";
        mm.runOnUiThread(new Runnable() {
            public void run() {
                if (progressDialog != null && progressDialog.isShowing())
                    progressDialog.dismiss();
                if (netplayDlg != null && netplayDlg.isShowing())
                    netplayDlg.hide();
                new WarnWidget.WarnWidgetHelper(mm,
                        mm.getString(R.string.np_drop_in_live, code), 4, Color.GREEN, false);
                Emulator.resume();
            }
        });
    }

    /** Somebody claimed the room: warn before the state transfer stops the
     *  picture, so a freeze mid-game reads as expected rather than as a bug. */
    private void warnDropInJoining() {
        if (!dropInLive) return;
        mm.runOnUiThread(new Runnable() {
            public void run() {
                new WarnWidget.WarnWidgetHelper(mm,
                        mm.getString(R.string.np_drop_in_joining), 3, Color.YELLOW, false);
            }
        });
    }
    /** One heartbeat: keeps the room alive and arms the punch when claimed. */
    private void pollLobby(LobbySession board, int gamePort) {
        LobbyClient.Endpoint peer = board.poll();

        if (peer == null) {
            if (!board.isPublished()) {
                /* The room went with a server recycle. Publishing again is the
                 * honest fix, and the user never has to know it happened. */
                publishOnLobby(gamePort);
                return;
            }
            int viewers = board.getViewers();
            lobbyLine = (viewers > 0)
                    ? mm.getString(R.string.np_lobby_watching, viewers)
                    : mm.getString(R.string.np_lobby_published);
            /* Field data: people were giving up after six seconds. Saying how
             * long this usually takes turns some of them into players. */
            lobbyLine += "\n" + mm.getString(R.string.np_lobby_patience);
            postHostMessage();
            return;
        }

        /* The peer comes back on every poll by design, but re-arming an
         * unchanged target only makes the network thread resolve it again. */
        String aim = (peer.sameSite && peer.lan.length > 0) ? peer.lan[0]
                : (peer.publicAddr != null) ? peer.publicAddr : peer.publicAlt;
        if (aim != null && aim.equals(lobbyAimedAt)) return;

        lobbyPeer = peer;
        if (LobbySession.aimAt(peer, gamePort)) {
            lobbyAimedAt = aim;
            lobbyLine = mm.getString(R.string.np_lobby_peer_found);
            postHostMessage();
            warnDropInJoining();
        }
    }

    /** What the board told us about the host we are joining, kept for the
     *  report: the joining side has no room of its own to read it from. */
    void setLobbyPeerInfo(String country, int mode, int delay, boolean sameSite,
                          boolean locked, LobbyClient.Nat peerNat) {
        joinPeerCountry = country;
        joinMode = mode;
        joinDelay = delay;
        joinSameSite = sameSite;
        joinLocked = locked;
        joinPeerNat = peerNat;
    }

    /** Outcome of a join that came from the board, from the joiner's side. */
    private void reportJoinOutcome(String outcome, long waitMs) {
        if (lobbyClaimRoom == null && joinPeerCountry == null) return;
        if (!LobbySession.isUsable(mm)) return;

        String info = Emulator.netplayGetPublicAddr();
        LobbyClient.Nat self = LobbySession.natOf(info, UpnpHelper.isMapped());

        /* The room is the host's: its game, mode, delay and privacy come from
         * the board, never from our own settings. No latency yet either -- the
         * game has not started, and a zero here is honest. */
        String game = (joinGameName != null && joinGameName.length() > 0)
                ? joinGameName : Emulator.getValueStr(Emulator.GAME_SELECTED);

        new LobbySession(mm).report(game, "client",
                outcome, self, joinPeerNat, LobbySession.pathOf(joinSameSite, UpnpHelper.isMapped()), waitMs,
                joinPeerCountry, joinMode, joinDelay, 0, 0, 0, 0, 0, joinLocked, joinRoom,
                joinIsDropIn);

        if ("connected".equals(outcome)) {
            sessionStartMs = System.currentTimeMillis();
            peerLeftCleanly = false;
            playedGame = game;
            playedRole = "client";
            playedPath = joinSameSite ? "lan" : "punch";
            playedPeerCountry = joinPeerCountry;
            playedSelfNat = self;
            playedPeerNat = joinPeerNat;
            playedMode = joinMode;
            playedDelay = joinDelay;
            playedLocked = joinLocked;
            playedDropIn = joinIsDropIn;
            playedRoom = joinRoom;
            startLatencySampler();
        }
        joinPeerCountry = null;
        /* Cleared with its siblings, not when joinGame reads it: the report
         * runs afterwards and needs to know which kind of session this was. */
        joinIsDropIn = false;
        joinGameName = null;
    }

    /**
     * Keeps the last live latency reading of a running session. Reading it at
     * the end is too late: the native handle is already torn down and every
     * counter reads zero, which is why the joining side reported nothing at
     * all. One sample a second describes the session, not its last instant.
     */
    private void startLatencySampler() {
        if (samplerRunning) return;
        samplerRunning = true;

        new Thread(new Runnable() {
            public void run() {
                while (samplerRunning && Emulator.getValue(Emulator.NETPLAY_HAS_CONNECTION) == 1) {
                    int rtt = Emulator.getValue(Emulator.NETPLAY_RTT);
                    if (rtt > 0) {
                        playedRtt = rtt;
                        playedJitter = Emulator.getValue(Emulator.NETPLAY_JITTER);
                        playedRttMin = Emulator.getValue(Emulator.NETPLAY_RTT_MIN);
                        playedRttMax = Emulator.getValue(Emulator.NETPLAY_RTT_MAX);
                    }
                    try {
                        Thread.sleep(1000);
                    } catch (InterruptedException e) {
                        Thread.currentThread().interrupt();
                        return;
                    }
                }

                /* Still armed means we are not the ones who stopped: the
                 * session ended on its own. This is the only hook that catches
                 * every way out -- exiting the game, the peer hanging up, a
                 * desync abort -- because the other two only run if the user
                 * happens to open the netplay dialog again afterwards. */
                if (samplerRunning) reportSessionEnded(endingOutcome());
                samplerRunning = false;
            }
        }).start();
    }

    /**
     * Report a finished game, once, with how long it actually lasted. Called
     * whenever a session ends -- by the Disconnect button or by the peer going
     * away -- and silently does nothing if the board was never involved.
     */

    /* The peer sent a DISCONNECT before going: a normal end of session, not a
     * connection that died. Set from the native warning, which is the only
     * place that sees the difference. */
    private volatile boolean peerLeftCleanly = false;

    public void notePeerLeftCleanly() {
        peerLeftCleanly = true;
    }

    /**
     * How this session ended, as far as we can honestly tell. Whoever presses
     * Disconnect reports "played" and the other used to report "dropped" for
     * the same session, so a normal game logged as half failure and the drop
     * rate meant nothing. A goodbye we received is the same outcome.
     */
    private String endingOutcome() {
        return peerLeftCleanly ? "played" : "dropped";
    }
    synchronized void reportSessionEnded(final String outcome) {
        /* Two paths can notice the same ending at once (the Disconnect button
         * and the watchdog below); whoever gets here first owns it. */
        final long started = sessionStartMs;
        if (started == 0) return;
        sessionStartMs = 0;

        if (!LobbySession.isUsable(mm)) return;
        final long playMs = System.currentTimeMillis() - started;

        /* The sampler's last live reading, not a fresh one: whoever ended the
         * session may well have torn the native handle down first, and then
         * every counter reads zero. */
        samplerRunning = false;
        final int rtt = playedRtt;
        final int jitter = playedJitter;
        final int rttMin = playedRttMin;
        final int rttMax = playedRttMax;
        playedRtt = playedJitter = playedRttMin = playedRttMax = 0;

        /* The room's own mode, delay and privacy, captured when the session
         * started -- not this device's settings. A joiner runs whatever the
         * host chose (JOIN_ACK is authoritative), and reading its own prefs
         * had the two sides describing one session differently. */
        final LobbySession board = new LobbySession(mm);
        board.report(playedGame, playedRole, outcome, playedSelfNat, playedPeerNat,
                playedPath, 0, playedPeerCountry, playedMode, playedDelay,
                playMs, rtt, jitter, rttMin, rttMax, playedLocked, playedRoom, playedDropIn);
    }

    /** Remembered between the board claiming a room and this device's STUN. */
    void setLobbyClaim(String base, String roomId, boolean playing, String game) {
        joinIsDropIn = playing;
        /* The room says what is being played. Our own GAME_SELECTED is what we
         * had loaded when we tapped, and on a board join it has not caught up
         * yet -- which is why the log showed host=kinst against client=dino
         * for one session. */
        joinGameName = game;
        lobbyClaimBase = base;
        lobbyClaimRoom = roomId;
        /* Kept apart: lobbyClaimRoom is consumed by the tuple correction,
         * while the report needs it until the session ends. */
        joinRoom = roomId;
        joinFromBoard = true;
    }

    /**
     * Hand the board our real address once the game socket has one. Runs on
     * the join worker, right after init; failing is silent, since the pairing
     * can still work from the tuple the host observes on our JOIN packets.
     */
    private void correctLobbyTuple(int localPort) {
        final String base = lobbyClaimBase;
        final String room = lobbyClaimRoom;
        lobbyClaimBase = null;
        lobbyClaimRoom = null;
        if (room == null) return;

        String info = Emulator.netplayGetPublicAddr();
        LobbySession.rememberNat(mm, info, UpnpHelper.isMapped());

        String[] tuples = LobbySession.publicTuples(info);
        if (tuples[0] == null) return;

        java.util.List<String> lan = new java.util.ArrayList<String>();
        for (String local : getAllLocalIPv4())
            lan.add(local + ":" + localPort);

        LobbyClient.Response result = LobbyClient.updatePeer(base, room,
                Emulator.netplayGetProtocolVersion(), LobbySession.appVersion(mm),
                tuples[0], tuples[1], lan,
                LobbySession.natOf(info, UpnpHelper.isMapped()), LobbySession.country(mm));

        /* If this does not land, the host punches at whatever guess we sent
         * when we claimed the room -- worth seeing in a field trace. */
        if (LobbyClient.DEBUG) Log.d("MAME4droid_Netplay", "lobby: corrected our tuple to " + tuples[0]
                + " on " + room + " -> status=" + result.status);
    }

    /** Withdraw the room and report how it went. */
    private void closeLobby(String outcome, long waitMs) {
        LobbySession board = lobby;
        LobbyClient.Endpoint peer = lobbyPeer;
        lobby = null;
        lobbyPeer = null;
        lobbyLine = null;
        if (board == null) return;

        /* On a fast pairing the peer's JOIN arrives before our next poll does,
         * so we would report the best case of all -- an instant LAN match --
         * with no idea who joined or how. One last ask before withdrawing. */
        if (peer == null && "connected".equals(outcome)) {
            LobbyClient.Endpoint late = board.poll();
            if (late != null) peer = late;
        }

        String roomId = board.getRoomId();
        board.close();

        /* Which route we ended up taking is the interesting half: it is what
         * turns "punching works unless a side is symmetric" into a measurement. */
        String path = (peer == null) ? null
                : LobbySession.pathOf(peer.sameSite, UpnpHelper.isMapped());
        board.report(Emulator.getValueStr(Emulator.GAME_SELECTED), "host", outcome,
                LobbySession.natOf(Emulator.netplayGetPublicAddr(), UpnpHelper.isMapped()),
                (peer != null) ? peer.nat : null, path, waitMs,
                (peer != null) ? peer.country : null,
                rollbackMode ? 1 : 0, mm.getPrefsHelper().getNetplayDelayValue(), 0,
                Emulator.getValue(Emulator.NETPLAY_RTT), Emulator.getValue(Emulator.NETPLAY_JITTER),
                Emulator.getValue(Emulator.NETPLAY_RTT_MIN), Emulator.getValue(Emulator.NETPLAY_RTT_MAX),
                lobbyPrivate, roomId, dropIn);

        /* From here the session either plays or dies; how long it lasts is
         * reported separately, since "connected" on its own cannot tell a real
         * game from a pairing that fell apart in ten seconds. */
        if ("connected".equals(outcome)) {
            sessionStartMs = System.currentTimeMillis();
            peerLeftCleanly = false;
            playedGame = Emulator.getValueStr(Emulator.GAME_SELECTED);
            playedRole = "host";
            playedPath = path;
            playedPeerCountry = (peer != null) ? peer.country : null;
            playedSelfNat = LobbySession.natOf(Emulator.netplayGetPublicAddr(), UpnpHelper.isMapped());
            playedPeerNat = (peer != null) ? peer.nat : null;
            playedMode = rollbackMode ? 1 : 0;
            playedDelay = mm.getPrefsHelper().getNetplayDelayValue();
            playedLocked = lobbyPrivate;
            playedDropIn = dropIn;
            playedRoom = roomId;
            startLatencySampler();
        }
    }

    /* Public-tuple lines for the waiting/connecting dialogs (worker thread,
     * after netplayInit returned).  The "unavailable" warning only shows
     * when internet play is actually in play, so LAN sessions stay clean.
     * The full diagnostics block stays NLOG-only. */
    private String publicInfoLines(boolean stunRan, boolean warnUnavailable) {
        StringBuilder sb = new StringBuilder();
        if (stunRan) {
            String info = Emulator.netplayGetPublicAddr();
            if (info != null && info.length() > 0) {
                String[] parts = info.split("\\|");
                /* A v6 tuple gets its own label: "public IP" would clash with
                 * the v4 private/public wording.  Mobile-only -> "internet";
                 * Wi-Fi with own v6 -> "same network or internet"; Wi-Fi but
                 * v6 via cellular -> flag the mobile-data cost explicitly. */
                sb.append('\n').append(mm.getString(!parts[0].startsWith("[")
                        ? R.string.np_public_ip
                        : getMainLocalIPv4() == null ? R.string.np_ipv6_inet
                        : hasNonCarrierGlobalV6() ? R.string.np_ipv6_addr
                        : R.string.np_ipv6_mobile, parts[0]));
                for (String p : parts)
                    if (p.startsWith("alt="))
                        sb.append('\n').append(mm.getString(R.string.np_public_ip, p.substring(4)));
                /* sym=1 comes from the v4 STUN leg: only warn when v4 IS the
                 * primary path.  With a v6 primary (Auto) the main route has
                 * no NAT, so the warning would just be misleading noise. */
                boolean v4primary = !parts[0].startsWith("[");
                boolean mobileOnly = getMainLocalIPv4() == null;
                int ipp = mm.getPrefsHelper().getNetplayIpProtocol();
                if (info.contains("sym=1") && v4primary) {
                    sb.append('\n').append(mm.getString(R.string.np_symmetric_nat));
                    /* On Wi-Fi v6 is uncertain -> suggest Auto (safe fallback). */
                    if (ipp == 0 && !mobileOnly)
                        sb.append('\n').append(mm.getString(R.string.np_try_auto));
                }
                /* Mobile CGNAT kills v4 punching far more often than the
                 * 2-server sym test can prove (covert per-destination mapping,
                 * field-tested) and carriers nearly always have v6: on
                 * mobile-only IPv4 the tip is warranted unconditionally. */
                if (ipp == 0 && v4primary && mobileOnly)
                    sb.append('\n').append(mm.getString(R.string.np_try_ipv6));
            } else if (warnUnavailable) {
                sb.append('\n').append(mm.getString(R.string.np_public_unavailable));
            }
        }
        return sb.toString();
    }

    Button.OnClickListener publicRoomsClick = new Button.OnClickListener() {
        public void onClick(View v) {
            showPublicRooms();
        }
    };

    Button.OnClickListener joinGameClick = new Button.OnClickListener() {
        public void onClick(View v) {
            Runnable action = new Runnable() { public void run() {
            AlertDialog.Builder alert = new AlertDialog.Builder(mm);

            alert.setTitle(mm.getString(R.string.np_enter_peer_ip));

            final EditText input = new EditText(mm);
            String ip = mm.getPrefsHelper().getSharedPreferences().getString(PrefsHelper.PREF_NETPLAY_PEERADDR, "");
            input.setText(ip);
            input.setSelection(input.getText().length());

            /* Custom view: hint + field + a Clear/Paste row (pasting the shared
             * invite by hand is fiddly, so give it a one-tap button). */
            float d = mm.getResources().getDisplayMetrics().density;
            LinearLayout box = new LinearLayout(mm);
            box.setOrientation(LinearLayout.VERTICAL);
            box.setPadding((int) (20 * d), (int) (8 * d), (int) (20 * d), 0);
            TextView hint = new TextView(mm);
            hint.setText(mm.getString(R.string.np_join_hint));
            hint.setPadding(0, 0, 0, (int) (8 * d));
            box.addView(hint);
            box.addView(input);
            LinearLayout btnRow = new LinearLayout(mm);
            btnRow.setOrientation(LinearLayout.HORIZONTAL);
            btnRow.setGravity(android.view.Gravity.END);
            Button clearBtn = new Button(mm);
            clearBtn.setText(mm.getString(R.string.clear));
            clearBtn.setOnClickListener(new View.OnClickListener() {
                public void onClick(View b) { input.setText(""); }
            });
            Button pasteBtn = new Button(mm);
            pasteBtn.setText(mm.getString(R.string.paste));
            pasteBtn.setOnClickListener(new View.OnClickListener() {
                public void onClick(View b) {
                    String clip = readClipboard();
                    if (clip != null && clip.length() > 0) {
                        input.setText(clip);
                        input.setSelection(input.getText().length());
                    }
                }
            });
            btnRow.addView(clearBtn);
            btnRow.addView(pasteBtn);
            box.addView(btnRow);
            alert.setView(box);

                    alert.setPositiveButton(mm.getString(R.string.ok), new DialogInterface.OnClickListener() {
                public void onClick(DialogInterface dialog, int whichButton) {
                    /* Tolerates a pasted Share message (labels, both lines). */
                    final String raw = input.getText().toString();
                    final String ip = extractAddress(raw);

                    if (ip.length() == 0) {
                        showNetplayError(mm.getString(R.string.np_invalid_ip));
                        return;
                    }

                    InputMethodManager imm = (InputMethodManager) mm.getSystemService(Service.INPUT_METHOD_SERVICE);
                    imm.hideSoftInputFromWindow(input.getWindowToken(), 0);

                    SharedPreferences sp = mm.getPrefsHelper().getSharedPreferences();
                    Editor edit = sp.edit();
                    edit.putString(PrefsHelper.PREF_NETPLAY_PEERADDR, ip);
                    edit.commit();

                    /* Only when BOTH addresses are present (our Share text) does
                     * resolveAndJoin probe our public IP; a single pasted IP is
                     * used as-is. */
                    resolveAndJoin(raw);
                }
            });

            alert.setNegativeButton(mm.getString(R.string.cancel), new DialogInterface.OnClickListener() {
                public void onClick(DialogInterface dialog, int whichButton) {
                    // Canceled.
                }
            });

            AlertDialog dlg = alert.create();
            dlg.getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_STATE_HIDDEN);
            dlg.show();
            } };
            if (ensureLocalNet(action)) action.run();
        }
    };

    Button.OnClickListener disconnectGameClick = new Button.OnClickListener() {
        public void onClick(View v) {
            reportSessionEnded("played");
            Emulator.setValue(Emulator.NETPLAY_HAS_CONNECTION, 0);
            releaseWifiLock();
            deleteUpnpMappingAsync();
            com.seleuco.mame4droid.widgets.StatsWidget.hide(mm);

            /* Drop-in: the room is what this button really ends -- there is no
             * peer, only a game on the board. The heartbeat would withdraw it
             * a second later, and that second is long enough for somebody to
             * claim a room that is gone. Safe to run twice. */
            if (dropInLive) {
                new Thread(new Runnable() {
                    public void run() {
                        closeLobby("cancelled", 0);
                    }
                }).start();
            }

            new WarnWidget.WarnWidgetHelper(mm, mm.getString(R.string.np_disconnected_game), 3, Color.YELLOW, false);
            prepareButtons();
        }
    };

    /* Mid-game state resync (rollback only): latches the episode natively
     * (host recaptures + streams its state; client adopts it) and closes
     * the dialog so emulation resumes straight into the sync freeze. */
    Button.OnClickListener resyncGameClick = new Button.OnClickListener() {
        public void onClick(View v) {
            if (Emulator.netplayResync() == 1) {
                netplayDlg.dismiss();
                Emulator.resume();
            } else {
                showNetplayError(mm.getString(R.string.np_resync_unavailable));
                prepareButtons();
            }
        }
    };

    public void createGame() {

        /* Hosting is what puts our address on the board, so ask before the
         * waiting dialog goes up. Either answer carries on hosting: declining
         * just means doing it the manual way, exactly as before. */
        Runnable retry = new Runnable() {
            public void run() {
                createGame();
            }
        };
        if (!ensureLobbyConsent(retry, retry)) return;

        String strPort = mm.getPrefsHelper().getNetplayPort();
        int port = 0;
        try {
            port = Integer.parseInt(strPort);
        } catch (Exception e) {
        }
        if (!(port >= 1024 && port <= 32768 * 2)) {
            showNetplayError(mm.getString(R.string.np_invalid_port));
            return;
        }
        final int gamePort = port;
        final int ipProto = mm.getPrefsHelper().getNetplayIpProtocol();

        /* Strict IPv6 but no usable v6 anywhere (incl. mobile): nothing to
         * share and no peer could reach us.  Refuse before opening any dialog
         * (AlertDialog sits above the NetPlay menu), pointing at IPv4/Auto. */
        if (ipProto == 1 && !hasUsableIPv6()) {
            showNetplayError(mm.getString(R.string.np_ipv6_none));
            return;
        }

        Emulator.netplaySetDesyncDetectorEnabled(mm.getPrefsHelper().isNetplayDesyncDetectorEnabled() ? 1 : 0);

        /* Apply BEFORE netplayInit(): netplay_init_handle() preserves
         * frame_skip exactly as it finds it, defaulting to 2 (=1 UI frame)
         * otherwise -- setting it later (once has_joined) mostly no-ops,
         * see netplay_ui_set_delay's "not yet joined" branch. */
        Emulator.setValue(Emulator.NETPLAY_DELAY, mm.getPrefsHelper().getNetplayDelayValue());

        canceled = false;
        /* Fresh per session: a previous drop-in must not leave the joining
         * notice armed for a host that is sitting in the waiting dialog. */
        dropInLive = false;
        publishWaitingSince = 0;
        AlertDialog.Builder waitBld = new AlertDialog.Builder(mm);
        waitBld.setTitle(mm.getString(R.string.np_press_back_cancel));
        waitBld.setView(buildProgressView(mm.getString(R.string.np_waiting_peer), mm.getString(R.string.np_getting_info)));
        waitBld.setCancelable(true);
        waitBld.setOnCancelListener(new DialogInterface.OnCancelListener() {
            @Override
            public void onCancel(DialogInterface dialog) {
                canceled = true;
            }
        });
        /* Null listeners at build time, real ones AFTER show(): stock alert
         * buttons auto-dismiss otherwise.  Share = system share sheet with
         * the addresses; Peer IP = hot punch-target entry (each side only
         * learns its own public tuple once its flow starts). */
        waitBld.setPositiveButton(mm.getString(R.string.np_btn_share), (DialogInterface.OnClickListener) null);
        waitBld.setNeutralButton(mm.getString(R.string.np_btn_peer_ip), (DialogInterface.OnClickListener) null);
        progressDialog = waitBld.create();
        /* Hosting is waiting with the phone in your hand for someone to show
         * up; a screen that blanks takes the app to the background and the
         * room with it. */
        if (progressDialog.getWindow() != null)
            progressDialog.getWindow().addFlags(
                    android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        progressDialog.show();
        final Button peerBtn = progressDialog.getButton(DialogInterface.BUTTON_NEUTRAL);
        if (peerBtn != null) {
            /* Off until init is done: a punch target set before the worker's
             * netplaySetPunchAddr(null,0) clear would be wiped, and internet
             * viability isn't known until STUN.  Re-enabled (or hidden) below. */
            peerBtn.setEnabled(false);
            peerBtn.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) {
                    promptHotPunchAddr(gamePort);
                }
            });
        }
        final Button shareBtn = progressDialog.getButton(DialogInterface.BUTTON_POSITIVE);
        if (shareBtn != null) {
            /* Off until the worker has BOTH addresses (local IP + STUN): an
             * early tap would share a half-empty or stale (previous-session)
             * message.  Re-enabled once postHostMessage() runs below. */
            shareBtn.setEnabled(false);
            shareBtn.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) {
                    shareAddresses(gamePort);
                }
            });
        }
        upnpFallbackHint = ""; /* set in the worker once the network shape is known */

        Thread t = new Thread(new Runnable() {
            public void run() {
                hostBaseMsg = null;
                upnpLine = null;

                final String ip = getMainLocalIPv4();
                /* Strict v6 socket never receives v4: sharing/showing v4
                 * LAN addresses there would hand out dead invites. */
                shareLocalAddr = (ipProto == 1) ? null : ip;
                sharingAsHost = true;

                /* UPnP and the port-forward hint only apply where a home
                 * router exists (LAN v4 present) and the socket receives v4
                 * (not strict v6): mobile data has no router to map. */
                if (ip != null && ipProto != 1) {
                    upnpFallbackHint = "\n" + mm.getString(R.string.np_upnp_fallback_hint, gamePort);
                    if (mm.getPrefsHelper().isNetplayUpnpEnabled()) {
                        /* Runs in parallel with init/STUN: asks the router to
                         * forward the game port (automates the port-forward
                         * fallback, rescues symmetric-NAT/CGNAT peers). */
                        new Thread(new Runnable() {
                            public void run() {
                                if (UpnpHelper.addPortMapping(gamePort)) {
                                    if (canceled) {
                                        UpnpHelper.deletePortMapping();
                                        return;
                                    }
                                    upnpLine = "\n" + mm.getString(R.string.np_upnp_mapped);
                                    postHostMessage();
                                }
                            }
                        }).start();
                    }
                }
                if (ip == null && !hasAnyIPv4() && !hasUsableIPv6()) {
                    try {
                        Thread.sleep(2000);
                    } catch (InterruptedException e) {
                        e.printStackTrace();
                    }
                    canceled = true;
                    mm.runOnUiThread(new Runnable() {
                        public void run() {
                            showNetplayError(mm.getString(R.string.np_no_network));
                        }
                    });
                }

                if (!canceled) {
                    /* Native init (socket+bind+STUN) blocks up to ~3s: it
                     * must run on this worker, never on the UI thread.
                     * The host ALWAYS runs STUN (its public address is the
                     * first thing to share); the punch target starts clear
                     * and is armed via the Peer IP button if ever needed. */
                    Emulator.netplaySetPunchAddr(null, 0);
                    Emulator.netplaySetLocalPort(gamePort);
                    Emulator.netplaySetInternetMode(1);
                    Emulator.netplaySetIpFamily(ipProto);
                    /* Before netplayInit: it decides whether the session begins
                     * now or waits for somebody, and the handle reset keeps it. */
                    Emulator.setValue(Emulator.NETPLAY_DROP_IN, dropIn ? 1 : 0);

                    if (Emulator.netplayInit(null, gamePort, 0) == -1) {
                        canceled = true;
                        mm.runOnUiThread(new Runnable() {
                            public void run() {
                                showNetplayError(mm.getString(R.string.np_error_init));
                            }
                        });
                    } else {
                        acquireWifiLock(); /* Radio at full power for the session */
                        /* STUN has run by now, under the family chosen just
                         * above: stamp it here so a manual session counts too,
                         * not only the ones that reach the board. */
                        LobbySession.rememberNat(mm, Emulator.netplayGetPublicAddr(),
                                UpnpHelper.isMapped());
                    }
                }

                if (!canceled) {
                    /* STUN can fail while local v6 addresses still exist: a
                     * global is public (no NAT, plays over the internet); a ULA
                     * plays on the same LAN only.  Show each with the right
                     * label instead of a bare "unavailable". */
                    boolean pubEmpty = Emulator.netplayGetPublicAddr().length() == 0;
                    java.util.List<String> v6glob = new java.util.ArrayList<String>();
                    java.util.List<String> v6lan = new java.util.ArrayList<String>();
                    if (pubEmpty && ipProto != 0)
                        for (String a : getAllLocalIPv6())
                            (isPrivateIPv6(a) ? v6lan : v6glob).add(a);
                    boolean noV6shown = v6glob.isEmpty() && v6lan.isEmpty();
                    /* Strict v6 that yielded nothing usable -- STUN got no public
                     * v6 AND there is no shareable local v6 (e.g. Wi-Fi with only
                     * link-local while the sole global sits on mobile and STUN
                     * routed out via Wi-Fi): refuse with the switch-to-IPv4/Auto
                     * message instead of a dead "waiting" dialog.  The STUN
                     * result is the real signal (the pre-init guard only knows
                     * an address exists, not whether it is reachable). */
                    if (ipProto == 1 && pubEmpty && noV6shown) {
                        canceled = true;
                        mm.runOnUiThread(new Runnable() {
                            public void run() {
                                showNetplayError(mm.getString(R.string.np_ipv6_none));
                            }
                        });
                    }

                    if (!canceled) {
                    boolean noInternet = pubEmpty && v6glob.isEmpty();
                    if (noInternet) {
                        /* No internet-reachable address: the Peer IP button and
                         * its UPnP hint would only mislead (LAN play still ok). */
                        upnpFallbackHint = "";
                        mm.runOnUiThread(new Runnable() {
                            public void run() {
                                if (peerBtn != null) peerBtn.setVisibility(View.GONE);
                            }
                        });
                    }

                    /* Strict v6 skips the v4 local/mobile-only line: the
                     * session lives on its own v6 addresses alone. */
                    String head = (ipProto == 1) ? ""
                            : (ip != null ? mm.getString(R.string.np_local_ip, ip)
                                          : mm.getString(R.string.np_mobile_only));
                    String pubLines = publicInfoLines(true,
                            (ip == null || ipProto == 1) && noV6shown);
                    for (String a : v6glob)
                        pubLines += "\n" + mm.getString(ip == null
                                ? R.string.np_ipv6_inet : R.string.np_ipv6_addr,
                                "[" + a + "]:" + gamePort);
                    if (!v6lan.isEmpty()) {
                        /* Spell out that internet play is off but LAN works. */
                        pubLines += "\n" + mm.getString(R.string.np_ipv6_no_public);
                        for (String a : v6lan)
                            pubLines += "\n" + mm.getString(R.string.np_ipv6_lan, "[" + a + "]:" + gamePort);
                    }
                    if (head.length() == 0 && pubLines.startsWith("\n"))
                        pubLines = pubLines.substring(1);
                    hostBaseMsg = head + pubLines
                            + "\n" + mm.getString(R.string.np_tap_share);
                    postHostMessage();

                    /* Data ready: enable the buttons (kept off until here so an
                     * early tap can't share a stale message or set a punch
                     * target that init's clear would wipe).  peerBtn may already
                     * be hidden above when there is no public IP. */
                    mm.runOnUiThread(new Runnable() {
                        public void run() {
                            if (shareBtn != null) shareBtn.setEnabled(true);
                            if (peerBtn != null) peerBtn.setEnabled(true);
                        }
                    });

                    /* Publish only now: the room advertises the tuple STUN
                     * just produced, and the board refuses one that is not
                     * really ours. */
                    if (!noInternet) publishOnLobby(gamePort);

                    /* Drop-in: hand the game straight back. The loop below
                     * carries on as the room's heartbeat, but nothing of
                     * netplay touches the machine until somebody joins -- the
                     * native side keeps has_begun_game at 0 until then. */
                    if (dropIn && lobby != null) goLiveForDropIn();
                    }
                }

                long waitStart = System.currentTimeMillis();
                int sinceLastPoll = 0;
                int sinceLastPublish = 0;
                while (Emulator.getValue(Emulator.NETPLAY_HAS_JOINED) == 0 && !canceled) {
                    try {
                        Thread.sleep(1000);
                        //System.out.println("Esperando...");
                    } catch (InterruptedException e) {
                        e.printStackTrace();
                    }
                    /* The board's heartbeat rides this loop rather than a
                     * thread of its own: while we are waiting here the room
                     * stays alive, and it dies by itself if we stop -- which
                     * is exactly what a trip to the background does. */

                    /* Drop-in lifecycle: the host can walk out with nobody
                     * having joined, and a room still advertising the game
                     * sends whoever answers into a wait that cannot end.
                     * Leaving force-disconnects natively, so has_connection is
                     * the signal -- and it ignores open MAME menus. */
                    if (dropInLive && !canceled
                            && Emulator.getValue(Emulator.NETPLAY_HAS_CONNECTION) == 0) {
                        canceled = true;
                        break;
                    }
                    LobbySession board = lobbyPaused ? null : lobby;
                    if (board != null && board.isPublished() && !canceled
                            && ++sinceLastPoll >= board.getPollSeconds()) {
                        sinceLastPoll = 0;
                        pollLobby(board, gamePort);
                    } else if (board == null && !lobbyPaused && !canceled && publishRetrySeconds > 0
                            && LobbySession.isUsable(mm)
                            && ++sinceLastPublish >= publishRetrySeconds) {
                        /* Without this the first host back after the server
                         * went to sleep never got on the board, while this
                         * very loop kept beating beside it. How soon to try
                         * again is set by whoever refused us -- see below. */
                        sinceLastPublish = 0;
                        publishOnLobby(gamePort);
                    }
                }
                hostWaitMs = System.currentTimeMillis() - waitStart;

                if (progressDialog != null && progressDialog.isShowing()) {
                    progressDialog.dismiss();
                }

                /* Either way the room has served its purpose: withdraw it so
                 * nobody joins a game that is over or never started. */
                closeLobby(canceled ? "cancelled" : "connected", hostWaitMs);

                if (canceled) {
                    Emulator.setValue(Emulator.NETPLAY_HAS_CONNECTION, 0);
                    releaseWifiLock();
                    UpnpHelper.deletePortMapping(); /* worker thread: sync ok */
                }

                mm.runOnUiThread(new Runnable() {
                    public void run() {
                        if (!canceled) {
                            if (netplayDlg.isShowing())
                                netplayDlg.hide();
                            /* A drop-in host is not starting anything: the game has
                             * been running all along and the joiner is being
                             * lifted into it. */
                            /* Drop-in gets a second longer: whoever reads this
                             * was playing, not waiting for it, and there is now
                             * a second line with where the other player is. */
                            new WarnWidget.WarnWidgetHelper(mm, mm.getString(dropInLive
                                    ? R.string.np_drop_in_joined : R.string.np_connected)
                                    + peerOriginSuffix(playedPeerCountry, playedPath),
                                    dropInLive ? 5 : 3, Color.GREEN, false);
                            Emulator.resume();
                        }
                    }
                });
            }
        });
        t.start();
    }

    public void joinGame(String addr) {

        /* Read before the validation returns below: leaving it armed would
         * hand the board's shorter deadline to the next hand-typed join. */
        final boolean fromBoard = joinFromBoard;
        final String fromRoom = fromBoard ? joinRoom : null;
        final boolean intoRunning = fromBoard && joinIsDropIn;
        final long joinDeadline = fromBoard
                ? JOIN_ANSWER_TIMEOUT_MS : JOIN_ANSWER_TIMEOUT_MANUAL_MS;
        joinFromBoard = false;

       String strPort = mm.getPrefsHelper().getNetplayPort();
        int port = 0;
        try {
            port = Integer.parseInt(strPort);
        } catch (Exception e) {
        }
        if (!(port >= 1024 && port <= 32768 * 2)) {
            showNetplayError(mm.getString(R.string.np_invalid_port));
            return;
        }

        /* Strict IPv6 but no usable v6 anywhere (incl. mobile): the join could
         * only time out on sendto.  Refuse up front (mirrors the host guard)
         * and point at the IPv4/Auto setting instead of a dead dialog. */
        if (mm.getPrefsHelper().getNetplayIpProtocol() == 1 && !hasUsableIPv6()) {
            showNetplayError(mm.getString(R.string.np_ipv6_none));
            return;
        }

        /* "ip[:port]": an explicit port overrides the pref as DESTINATION
         * (the host's NAT may have rewritten it into the shared tuple). */
        String[] hp = splitHostPort(addr.trim());
        final String destHost = hp[0];
        final int localPort = port; /* OUR bind port stays the settings one */
        int dp = port;
        if (hp[1] != null) { try { dp = Integer.parseInt(hp[1]); } catch (Exception e) {} }
        final int destPort = dp;
        final boolean destV4 = isIPv4Address(destHost);
        final boolean destV6 = isIPv6Address(destHost);
        final int ipProto = mm.getPrefsHelper().getNetplayIpProtocol();

        /* Validate BEFORE any socket: garbage or a family the strict socket
         * can't reach would only surface as a cryptic init error.  Hostnames
         * pass (resolved on the worker); Auto accepts both families. */
        if (destHost.length() == 0 || (!destV4 && !destV6 && !looksLikeHostname(destHost))) {
            showNetplayError(mm.getString(R.string.np_invalid_ip));
            return;
        }
        if ((ipProto == 1 && destV4) || (ipProto == 0 && destV6)) {
            showNetplayError(mm.getString(R.string.np_ip_family_mismatch,
                    destV4 ? "IPv4" : "IPv6", ipProto == 1 ? "IPv6" : "IPv4"));
            return;
        }

        /* Auto lets a v6 destination through, but only a device with v6 can
         * dial one: without it sendto answers "network unreachable" and the
         * dialog just sits there. The setting allowed it; the network does
         * not, and those are different questions. */
        if (destV6 && !hasUsableIPv6()) {
            showNetplayError(mm.getString(R.string.np_dest_needs_v6));
            return;
        }

        final boolean inetMode = destV6 ? !isPrivateIPv6(destHost) : !isPrivateIPv4(destHost);
        final String addrShown = addr;

        Emulator.netplaySetDesyncDetectorEnabled(mm.getPrefsHelper().isNetplayDesyncDetectorEnabled() ? 1 : 0);

        /* Apply BEFORE netplayInit() -- see the matching comment in
         * createGame(). Note the Client's value only matters until
         * JOIN_ACK: the Host is authoritative and overwrites it then. */
        Emulator.setValue(Emulator.NETPLAY_DELAY, mm.getPrefsHelper().getNetplayDelayValue());

        canceled = false;
        shareLocalAddr = null; /* the client only shares its public tuple */
        sharingAsHost = false;

        AlertDialog.Builder joinBld = new AlertDialog.Builder(mm);
        joinBld.setTitle(mm.getString(R.string.np_press_back_cancel));
        joinBld.setView(buildProgressView(mm.getString(R.string.np_connecting_to, addr),
                inetMode ? "" : lanConnectBody()));
        joinBld.setCancelable(true);
        joinBld.setOnCancelListener(new DialogInterface.OnCancelListener() {
            @Override
            public void onCancel(DialogInterface dialog) {
                canceled = true;
            }
        });
        /* A LAN join has nothing to share, and one that came from the board
         * has nobody to share it with: the room carried our tuple up and the
         * host is already punching at it. Offering it there reads as "the
         * automatic part did not work", which is the opposite of the truth. */
        if (inetMode && !fromBoard)
            joinBld.setPositiveButton(mm.getString(R.string.np_btn_share), (DialogInterface.OnClickListener) null);
        else if (fromBoard)
            /* Back already cancels, as the title says, but without a button
             * the board's dialog sits a good deal shorter than the manual one
             * and reads as though something is missing from it. A real
             * listener here: the cancel listener above only fires for back,
             * not for a button, and the worker watches this flag. */
            joinBld.setNegativeButton(mm.getString(R.string.cancel),
                    new DialogInterface.OnClickListener() {
                        public void onClick(DialogInterface d, int w) {
                            canceled = true;
                        }
                    });
        progressDialog = joinBld.create();
        progressDialog.show();
        Button shareBtn = progressDialog.getButton(DialogInterface.BUTTON_POSITIVE);
        if (shareBtn != null) {
            shareBtn.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) {
                    shareAddresses(destPort);
                }
            });
        }

        Thread t = new Thread(new Runnable() {
            public void run() {
                /* Native init (socket+bind+STUN) blocks up to ~3s: worker
                 * only.  The punch target is host-side config; clear any
                 * stale one from a previous hosted session. */
                Emulator.netplaySetPunchAddr(null, 0);
                Emulator.netplaySetLocalPort(localPort);
                Emulator.netplaySetInternetMode(inetMode ? 1 : 0);
                Emulator.netplaySetIpFamily(ipProto);
                /* Joining is never a drop-in: only the host holds a game open. */
                Emulator.setValue(Emulator.NETPLAY_DROP_IN, 0);

                if (Emulator.netplayInit(destHost, destPort, 0) == -1) {
                    canceled = true;
                    mm.runOnUiThread(new Runnable() {
                        public void run() {
                            showNetplayError(mm.getString(R.string.np_error_init));
                        }
                    });
                } else {
                    acquireWifiLock(); /* Radio at full power for the session */
                    LobbySession.rememberNat(mm, Emulator.netplayGetPublicAddr(),
                            UpnpHelper.isMapped());

                    /* Now, and only now, do we know our real tuple: the board
                     * got a guess when we claimed the room, and over IPv6 not
                     * even that. Correcting it is what lets the host punch back
                     * at us, which on two mobile connections is the whole game. */
                    correctLobbyTuple(localPort);

                    String pub = publicInfoLines(inetMode, inetMode);
                    if (pub.startsWith("\n")) pub = pub.substring(1);
                    /* Own public IP == join target: both devices sit behind
                     * the same router (at home the two tuples even match
                     * char by char) -- the LAN address is the way then. */
                    String sameNet = "";
                    String info = Emulator.netplayGetPublicAddr();
                    if (info != null && info.length() > 0) {
                        String myPubIp = info.split("\\|")[0];
                        if (myPubIp.startsWith("[")) { /* "[v6]:port" form */
                            int e = myPubIp.indexOf(']');
                            myPubIp = e > 1 ? myPubIp.substring(1, e) : myPubIp;
                        } else {
                            int c = myPubIp.indexOf(':');
                            if (c > 0) myPubIp = myPubIp.substring(0, c);
                        }
                        if (myPubIp.equalsIgnoreCase(destHost))
                            sameNet = "\n" + mm.getString(R.string.np_same_public_ip);
                    }
                    /* Which room this address belongs to. The dialog title
                     * carries the address, and on a board with several games
                     * of the same name that is not enough to know whose room
                     * you just walked into. */
                    String room = (fromRoom != null && fromRoom.length() > 0)
                            ? mm.getString(R.string.np_lobby_room_code, fromRoom) + "\n\n"
                            : "";

                    /* Same rule as the Share button above: a board join has
                     * already handed the host our tuple, so telling the user
                     * to send it by hand would invent a problem. It gets the
                     * reassuring version instead -- and the dialog would look
                     * abandoned with the address alone in it. */
                    final String msg = room + (inetMode
                            ? pub + sameNet + "\n\n" + mm.getString(fromBoard
                                ? R.string.np_board_join_body
                                : R.string.np_share_hint_client)
                            : lanConnectBody());
                    mm.runOnUiThread(new Runnable() {
                        public void run() {
                            if (progressText != null)
                                progressText.setText(msg);
                        }
                    });
                }

                long joinStart = System.currentTimeMillis();
                boolean unanswered = false;
                while (Emulator.getValue(Emulator.NETPLAY_HAS_JOINED) == 0
                        && !canceled) {
                    try {
                        if (Emulator.netplayInit(null, 0, 1) == -1)
                            canceled = true;
                        Thread.sleep(1000);
                        //System.out.println("Esperando...");
                    } catch (InterruptedException e) {
                        e.printStackTrace();
                    }
                    /* JOIN_ACK is one round trip away and both games are
                     * already running, so a slow ROM cannot show up here.
                     * Retrying for ever only froze two dialogs with nothing
                     * on screen to say the answer was never coming back. */
                    if (!canceled && Emulator.getValue(Emulator.NETPLAY_HAS_JOINED) == 0
                            && System.currentTimeMillis() - joinStart > joinDeadline) {
                        unanswered = true;
                        canceled = true;
                    }
                }

                /* Both ends report, or the numbers only ever describe hosts --
                 * and it is the joining side that feels a pairing fail. */
                reportJoinOutcome(unanswered ? "timeout"
                                : canceled ? "cancelled" : "connected",
                        System.currentTimeMillis() - joinStart);

                if (progressDialog != null && progressDialog.isShowing()) {
                    progressDialog.dismiss();
                }

                if (canceled) {
                    Emulator.setValue(Emulator.NETPLAY_HAS_CONNECTION, 0);
                    releaseWifiLock();
                }

                final boolean noAnswer = unanswered;
                mm.runOnUiThread(new Runnable() {
                    public void run() {
                        /* Say why we stopped. Silence here is what turned a
                         * broken return path into two frozen dialogs. */
                        if (noAnswer) {
                            showNetplayError(mm.getString(R.string.np_no_answer));
                            return;
                        }
                        if (!canceled) {
                            if (netplayDlg.isShowing())
                                netplayDlg.hide();
                            /* Walking into a game already running is not the same
                             * event as starting one together, and the wait that
                             * follows is a state transfer, not a boot. */
                            new WarnWidget.WarnWidgetHelper(mm, mm.getString(intoRunning
                                    ? R.string.np_drop_in_entered : R.string.np_connected)
                                    + peerOriginSuffix(joinPeerCountry,
                                        LobbySession.pathOf(joinSameSite, UpnpHelper.isMapped())),
                                    intoRunning ? 5 : 3, Color.GREEN, false);
                            Emulator.resume();
                        }
                    }
                });
            }
        });
        t.start();
    }

}
