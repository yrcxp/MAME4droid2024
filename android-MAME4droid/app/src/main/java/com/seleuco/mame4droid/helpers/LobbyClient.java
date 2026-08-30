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

import android.util.Log;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.ArrayList;
import java.util.List;

/**
 * Client for the netplay lobby: the public board where a host advertises a
 * game and a peer picks it up. It only automates the tuple exchange done by
 * hand with Share / Peer IP, so every failure here is silent and the manual
 * path is untouched. Blocking network I/O: worker threads only.
 */
public class LobbyClient {

    private static final String TAG = "MAME4droid_Netplay";

    /* Lobby chatter: one line per poll every few seconds, invaluable in a field
     * trace and pure noise in a shipped build.  NETPLAY_LOG_ENABLED only
     * silences the native side; javac strips these branches when false.
     * KEEP false FOR RELEASE. */
    static final boolean DEBUG = false;

    /** Public board. Overridable from the prefs so anyone can host their own. */
    public static final String DEFAULT_URL =
            "https://mamelobby-api-fse4a3gbehavfdhd.westcentralus-01.azurewebsites.net";

    /* A free server unloads after some minutes idle and is slow to wake, so
     * the first call of a session can take seconds. That is a spinner, not an
     * error, and the timeout has to leave room for it. */
    private static final int CONNECT_TIMEOUT_MS = 15000;
    private static final int READ_TIMEOUT_MS = 15000;

    /* Every response is small by design; anything bigger is not ours. */
    private static final int MAX_RESPONSE_BYTES = 64 * 1024;

    /* Our own network fingerprint, for telling a LAN room from a remote one. */
    private static final String SITE_HEADER = "X-Lobby-Site";

    private static final int STATUS_NO_NETWORK = 0;

    private LobbyClient() {
    }

    /** Server-driven behaviour, fetched before anything else is shown. */
    public static class Config extends Response {
        /* True only when our own server answered. A 503 from the host while
         * the server wakes carries an HTML body, so nothing here gets filled
         * in -- and taking that for a deliberate shutdown is how a cold start
         * became "the board is not available". */
        public boolean answered;

        public boolean enabled;
        public int pollSeconds = 3;
        public int listSeconds = 5;
        public double listBackoff = 1.5;
        public int listMaxSeconds = 20;
        public String minApp = "";
        public String notice = "";
        public boolean updateAvailable;

        /* Null unless the server had something worth showing: every figure is
         * already past its own threshold there, so nothing here needs judging
         * again before it is drawn. */
        public Stats stats;
    }

    /**
     * Recent activity behind the board, so an empty list reads as "you are
     * early" rather than "this is dead". A zero means the server chose not to
     * show that figure, not that nothing happened.
     */
    public static class Stats {
        public String since = "";
        public int rooms;
        public int played;
        public String[] games = new String[0];
        public int countries;

        /** ISO-3166 pairs for the busiest few, drawn as flags. */
        public String[] flags = new String[0];
    }

    /**
     * NAT quality of one side, straight from its own STUN pass. sym and pp
     * describe the IPv4 NAT, the only family that has one; v6 says this peer
     * reached STUN over IPv6 and would dial that way, which needs no punching
     * at all whatever its v4 looks like.
     */
    public static class Nat {
        public boolean sym;
        public boolean pp;
        public boolean upnp;
        public boolean v6;

        /* No address of our own except the carrier's: mobile data, where there
         * is no router to ask for a forward and nothing the user can open.
         * Ours only, like symKnown -- a peer's flags say what they measured. */
        public boolean mob;

        /* Whether sym was measured at all. The symmetric test is a second v4
         * STUN query, so a device that only ever asked over IPv6 reports the
         * initialised false and not a finding. Ours only: it never goes on the
         * wire, and a peer's flags arrive with nothing to judge them by. */
        public boolean symKnown = true;

        public Nat() {
        }

        public Nat(boolean sym, boolean pp, boolean upnp, boolean v6) {
            this(sym, pp, upnp, v6, true);
        }

        public Nat(boolean sym, boolean pp, boolean upnp, boolean v6, boolean symKnown) {
            this.sym = sym;
            this.pp = pp;
            this.upnp = upnp;
            this.v6 = v6;
            this.symKnown = symKnown;
        }
    }

    /** One published game, as the board shows it: never an address. */
    public static class Room {
        public String id;
        public String game;
        public String country;
        public String app;
        public int mode;
        public int delay;
        public boolean plugins;
        public boolean verified;
        public boolean hasLan;
        public Nat nat = new Nat();
        public long since;

        /* Salted hash of the host's address. Equal to our own (the board's
         * mySite) means one router between us, so it is a LAN game. */
        public String site;

        /* Needs the host's PIN to join. The PIN itself never reaches the board. */
        public boolean locked;

        /* Drop-in: the host is playing right now, and joining lifts you into
         * the running game with a state transfer instead of starting one. */
        public boolean playing;
    }

    public static class Board extends Response {
        public final List<Room> rooms = new ArrayList<Room>();
        public int total;
        public String etag;
        public boolean notModified;

        /* The server's own clock, so "waiting for N seconds" never turns
         * negative on a device whose date is off. */
        public long serverTimeSeconds;

        /* Our own site hash, for spotting rooms on our network. It rides a
         * header rather than the body so every viewer shares one ETag, and it
         * still arrives with a 304. */
        public String mySite;
    }

    public static class Created extends Response {
        /** Server-side reason for a refusal, e.g. "too_many_rooms". */
        public String error;

        public String id;
        public String token;
        public int ttl;
        public int pollSeconds;
        public boolean verified;
    }

    /** The other side's rendezvous data, whichever side that is. */
    public static class Endpoint {
        public String publicAddr;
        public String publicAlt;
        public String[] lan = new String[0];
        public Nat nat = new Nat();
        public String country;
        public boolean verified;

        /* Both of us reached the lobby from one public address, so we are
         * behind the same router and the LAN tuples are the ones to dial. */
        public boolean sameSite;

        /* Host only: what the client has to load to play along. */
        public String game;
        public int mode;
        public int delay;
        public boolean plugins;
    }

    public static class JoinResult extends Response {
        public Endpoint host;
    }

    public static class PollResult extends Response {
        public String state;
        public int ttl;
        public int viewers;
        public Endpoint peer;
    }

    /** HTTP outcome shared by every call; status 0 means it never got out. */
    public static class Response {
        public int status;

        /** Seconds the server asked us to wait, 0 if it did not say. */
        public int retryAfterSeconds;

        public boolean ok() {
            return status >= 200 && status < 300;
        }

        /** The board is switched off server-side; say so, do not retry. */
        public boolean disabled() {
            return status == 503;
        }

        /**
         * The host answering for a server that is switched off: 403 in under
         * a second, whether it was stopped on purpose or ran out of its daily
         * allowance. A cold start looks nothing like it -- that one hangs, or
         * answers 503.
         */
        public boolean siteOff() {
            return status == 403;
        }

        /** Nothing to apologise for: no network, or the board is unreachable. */
        public boolean offline() {
            return status == STATUS_NO_NETWORK;
        }

        /**
         * Over the per-address budget. Everyone behind one router shares it,
         * so a second player at home can hit this with nothing wrong at all.
         * It is the server answering, not a sleeping one.
         */
        public boolean throttled() {
            return status == 429;
        }

        /** Honour Retry-After, with a floor: hammering is what got us here. */
        public long backoffMs() {
            int seconds = Math.max(retryAfterSeconds, 30);
            return Math.min(seconds, 120) * 1000L;
        }
    }

    /**
     * Cheapest call there is: it touches no state and is exempt from the kill
     * switch, so it doubles as the way to get a sleeping server loading before
     * anyone needs an answer from it.
     */
    public static Response health(String base) {
        Response out = new Response();
        out.status = get(base, "/api/v1/health", null).status;
        return out;
    }

    public static Config config(String base, int proto, String app) {
        Config out = new Config();
        Http http = get(base, "/api/v1/config?proto=" + proto + "&app=" + enc(app), null);
        out.status = http.status;
        out.retryAfterSeconds = http.retryAfterSeconds;
        JSONObject body = http.json();
        if (body == null) return out;

        out.answered = true;
        out.enabled = body.optBoolean("enabled", false);
        out.pollSeconds = clampSeconds(body.optInt("pollSeconds", out.pollSeconds), 1, 60);
        out.listSeconds = clampSeconds(body.optInt("listSeconds", out.listSeconds), 1, 300);
        out.listBackoff = body.optDouble("listBackoff", out.listBackoff);
        out.listMaxSeconds = clampSeconds(body.optInt("listMaxSeconds", out.listMaxSeconds), 1, 300);
        out.minApp = body.optString("minApp", "");
        out.notice = body.optString("notice", "");
        out.updateAvailable = body.optBoolean("updateAvailable", false);

        JSONObject stats = body.optJSONObject("stats");
        if (stats != null) {
            Stats recent = new Stats();
            recent.since = stats.optString("since", "");
            recent.rooms = stats.optInt("rooms", 0);
            recent.played = stats.optInt("played", 0);
            recent.games = readStrings(stats.optJSONArray("games"));
            recent.countries = stats.optInt("countries", 0);
            recent.flags = readStrings(stats.optJSONArray("flags"));
            out.stats = recent;
        }
        return out;
    }

    private static String[] readStrings(JSONArray array) {
        if (array == null) return new String[0];
        List<String> values = new ArrayList<String>(array.length());
        for (int i = 0; i < array.length(); i++) {
            String value = array.optString(i, "");
            if (value.length() > 0) values.add(value);
        }
        return values.toArray(new String[0]);
    }

    /**
     * The board for one protocol. Pass the previous {@code etag} back: an
     * unchanged board then answers 304 with no body, which is what keeps a
     * screen left open inside the server traffic allowance.
     */
    public static Board list(String base, int proto, String etag) {
        Board out = new Board();
        Http http = get(base, "/api/v1/rooms?proto=" + proto, etag);
        out.status = http.status;
        out.retryAfterSeconds = http.retryAfterSeconds;
        out.etag = http.etag != null ? http.etag : etag;
        out.serverTimeSeconds = http.dateSeconds;
        out.mySite = http.site;

        if (http.status == HttpURLConnection.HTTP_NOT_MODIFIED) {
            out.notModified = true;
            return out;
        }

        JSONObject body = http.json();
        if (body == null) return out;

        out.total = body.optInt("total", 0);
        JSONArray rooms = body.optJSONArray("rooms");
        for (int i = 0; rooms != null && i < rooms.length(); i++) {
            JSONObject item = rooms.optJSONObject(i);
            if (item == null) continue;
            Room room = new Room();
            room.id = item.optString("id", "");
            room.game = item.optString("game", "");
            room.country = emptyToNull(item.optString("country", ""));
            room.app = item.optString("app", "");
            room.mode = item.optInt("mode", 0);
            room.delay = item.optInt("delay", 0);
            room.plugins = item.optBoolean("plugins", false);
            room.verified = item.optBoolean("verified", false);
            room.hasLan = item.optBoolean("hasLan", false);
            room.nat = readNat(item.optJSONObject("nat"));
            room.since = item.optLong("since", 0);
            room.site = item.optString("site", "");
            room.locked = item.optBoolean("locked", false);
            room.playing = item.optBoolean("playing", false);
            if (room.id.length() > 0 && room.game.length() > 0)
                out.rooms.add(room);
        }
        return out;
    }

    /** Publish a game. The advertised address must be ours or it is refused. */
    public static Created create(String base, int proto, String app, String game,
                                 int mode, int delay, boolean plugins,
                                 String publicAddr, String publicAlt, List<String> lan,
                                 Nat nat, String country, String pin, boolean playing) {
        Created out = new Created();
        try {
            JSONObject body = new JSONObject();
            body.put("proto", proto);
            body.put("app", app);
            body.put("game", game);
            body.put("mode", mode);
            body.put("delay", delay);
            body.put("plugins", plugins);
            body.put("public", publicAddr);
            if (publicAlt != null) body.put("publicAlt", publicAlt);
            body.put("lan", new JSONArray(lan));
            body.put("nat", writeNat(nat));
            if (country != null) body.put("country", country);
            if (pin != null) body.put("pin", pin);
            if (playing) body.put("playing", true);

            Http http = send(base, "/api/v1/rooms", "POST", body);
            out.status = http.status;
            JSONObject answer = http.json();
            if (answer == null) return out;

            out.error = answer.optString("error", "");
            out.id = answer.optString("id", "");
            out.token = answer.optString("token", "");
            out.ttl = answer.optInt("ttl", 60);
            out.pollSeconds = answer.optInt("pollSeconds", 3);
            out.verified = answer.optBoolean("verified", false);
        } catch (Exception e) {
            if (DEBUG) Log.d(TAG, "lobby: create failed: " + e);
        }
        return out;
    }

    /**
     * Claim a room and hand over our own tuple in the same call. Punching is
     * symmetric: without our half the host could never aim back at us, and
     * every NAT but full cone would fail.
     */
    public static JoinResult join(String base, String id, int proto, String app,
                                  String publicAddr, String publicAlt, List<String> lan,
                                  Nat nat, String country, String pin, String claim) {
        JoinResult out = new JoinResult();
        try {
            JSONObject body = new JSONObject();
            body.put("proto", proto);
            body.put("app", app);
            if (publicAddr != null) body.put("public", publicAddr);
            if (publicAlt != null) body.put("publicAlt", publicAlt);
            body.put("lan", new JSONArray(lan));
            body.put("nat", writeNat(nat));
            if (country != null) body.put("country", country);
            if (pin != null) body.put("pin", pin);
            if (claim != null) body.put("claim", claim);

            Http http = send(base, "/api/v1/rooms/" + enc(id) + "/join", "POST", body);
            out.status = http.status;
            JSONObject answer = http.json();
            if (answer == null) return out;
            out.host = readEndpoint(answer.optJSONObject("host"));
        } catch (Exception e) {
            if (DEBUG) Log.d(TAG, "lobby: join failed: " + e);
        }
        return out;
    }

    /**
     * Correct the tuple we left when we claimed the room, once our own STUN
     * pass has told us what it really is. Before that all we can offer is a
     * guess, and over IPv6 not even that: the pre-join probe is v4-only. The
     * host picks this up on its next poll and re-aims.
     */
    public static Response updatePeer(String base, String id, int proto, String app,
                                      String publicAddr, String publicAlt, List<String> lan,
                                      Nat nat, String country, String claim) {
        Response out = new Response();
        try {
            JSONObject body = new JSONObject();
            body.put("proto", proto);
            body.put("app", app);
            if (publicAddr != null) body.put("public", publicAddr);
            if (publicAlt != null) body.put("publicAlt", publicAlt);
            body.put("lan", new JSONArray(lan));
            body.put("nat", writeNat(nat));
            if (country != null) body.put("country", country);
            if (claim != null) body.put("claim", claim);

            out.status = send(base, "/api/v1/rooms/" + enc(id) + "/peer", "POST", body).status;
        } catch (Exception e) {
            if (DEBUG) Log.d(TAG, "lobby: peer update failed: " + e);
        }
        return out;
    }

    /**
     * Host heartbeat and peer delivery in one call: while this is polling the
     * room stays alive, and the answer carries the peer as soon as somebody
     * claims it. Safe to repeat &mdash; the peer keeps coming back until the
     * room is deleted, so a lost answer on a flaky link costs nothing.
     */
    public static PollResult poll(String base, String id, String token) {
        PollResult out = new PollResult();
        try {
            JSONObject body = new JSONObject();
            body.put("token", token);

            Http http = send(base, "/api/v1/rooms/" + enc(id) + "/poll", "POST", body);
            out.status = http.status;
            JSONObject answer = http.json();
            if (answer == null) return out;

            out.state = answer.optString("state", "open");
            out.ttl = answer.optInt("ttl", 0);
            out.viewers = answer.optInt("viewers", 0);
            out.peer = readEndpoint(answer.optJSONObject("peer"));
        } catch (Exception e) {
            if (DEBUG) Log.d(TAG, "lobby: poll failed: " + e);
        }
        return out;
    }

    /** Take the room off the board; it also dies on its own within a minute. */
    public static boolean delete(String base, String id, String token) {
        Http http = send(base, "/api/v1/rooms/" + enc(id) + "?token=" + enc(token),
                "DELETE", null);
        return http.status >= 200 && http.status < 300;
    }

    /**
     * How the session ended. This is what turns the rule of thumb ("punching
     * works unless a side is symmetric") into something measured. Anonymous,
     * and failing to send it must never be visible to the user.
     */
    public static void telemetry(String base, int proto, String app, String game,
                                 String role, String outcome, Nat self, Nat peer,
                                 String path, long waitMs, String country, String peerCountry,
                                 int mode, int delay, long playMs, int rttMs, int jitterMs,
                                 int rttMinMs, int rttMaxMs, boolean locked, String room,
                                 boolean dropIn) {
        try {
            JSONObject body = new JSONObject();
            body.put("proto", proto);
            body.put("app", app);
            body.put("game", game);
            body.put("role", role);
            body.put("outcome", outcome);
            if (self != null) body.put("natSelf", writeNat(self));
            if (peer != null) body.put("natPeer", writeNat(peer));
            if (path != null) body.put("path", path);
            body.put("waitMs", waitMs);
            if (country != null) body.put("country", country);
            if (peerCountry != null) body.put("peerCountry", peerCountry);
            body.put("mode", mode);
            body.put("delay", delay);
            body.put("playMs", playMs);
            body.put("rttMs", rttMs);
            body.put("jitterMs", jitterMs);
            body.put("rttMinMs", rttMinMs);
            body.put("rttMaxMs", rttMaxMs);
            body.put("locked", locked);
            if (room != null) body.put("room", room);
            if (dropIn) body.put("dropIn", true);
            send(base, "/api/v1/telemetry", "POST", body);
        } catch (Exception e) {
            if (DEBUG) Log.d(TAG, "lobby: telemetry failed: " + e);
        }
    }

    private static Endpoint readEndpoint(JSONObject json) {
        if (json == null) return null;
        Endpoint out = new Endpoint();
        out.publicAddr = emptyToNull(json.optString("public", ""));
        out.publicAlt = emptyToNull(json.optString("publicAlt", ""));
        out.nat = readNat(json.optJSONObject("nat"));
        out.country = emptyToNull(json.optString("country", ""));
        out.verified = json.optBoolean("verified", false);
        out.sameSite = json.optBoolean("sameSite", false);
        out.game = emptyToNull(json.optString("game", ""));
        out.mode = json.optInt("mode", 0);
        out.delay = json.optInt("delay", 0);
        out.plugins = json.optBoolean("plugins", false);

        JSONArray lan = json.optJSONArray("lan");
        if (lan != null) {
            List<String> addresses = new ArrayList<String>(lan.length());
            for (int i = 0; i < lan.length(); i++) {
                String address = lan.optString(i, "");
                if (address.length() > 0) addresses.add(address);
            }
            out.lan = addresses.toArray(new String[0]);
        }
        return out;
    }

    private static Nat readNat(JSONObject json) {
        Nat out = new Nat();
        if (json != null) {
            out.sym = json.optBoolean("sym", false);
            out.pp = json.optBoolean("pp", false);
            out.upnp = json.optBoolean("upnp", false);
            out.v6 = json.optBoolean("v6", false);
            out.mob = json.optBoolean("mob", false);
        }
        return out;
    }

    private static JSONObject writeNat(Nat nat) throws Exception {
        JSONObject out = new JSONObject();
        out.put("sym", nat != null && nat.sym);
        out.put("pp", nat != null && nat.pp);
        out.put("upnp", nat != null && nat.upnp);
        out.put("v6", nat != null && nat.v6);
        out.put("mob", nat != null && nat.mob);
        return out;
    }

    private static Http get(String base, String path, String etag) {
        return request(base, path, "GET", null, etag);
    }

    private static Http send(String base, String path, String method, JSONObject body) {
        return request(base, path, method, body, null);
    }

    private static Http request(String base, String path, String method,
                                JSONObject body, String etag) {
        Http out = new Http();
        HttpURLConnection connection = null;
        try {
            connection = (HttpURLConnection) new URL(trimSlash(base) + path).openConnection();
            connection.setRequestMethod(method);
            connection.setConnectTimeout(CONNECT_TIMEOUT_MS);
            connection.setReadTimeout(READ_TIMEOUT_MS);
            connection.setRequestProperty("Accept", "application/json");
            if (etag != null && etag.length() > 0)
                connection.setRequestProperty("If-None-Match", etag);

            if (body != null) {
                byte[] payload = body.toString().getBytes("UTF-8");
                connection.setDoOutput(true);
                connection.setFixedLengthStreamingMode(payload.length);
                connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
                OutputStream stream = connection.getOutputStream();
                stream.write(payload);
                stream.close();
            }

            out.status = connection.getResponseCode();
            out.etag = connection.getHeaderField("ETag");
            out.site = connection.getHeaderField(SITE_HEADER);
            out.dateSeconds = connection.getHeaderFieldDate("Date", 0L) / 1000L;
            out.retryAfterSeconds = connection.getHeaderFieldInt("Retry-After", 0);
            out.body = readAll(out.status < 400
                    ? connection.getInputStream() : connection.getErrorStream());
        } catch (Exception e) {
            /* Offline, DNS, a cold instance that took too long: the board is
             * optional, so this is a quiet nothing, never an error dialog. */
            if (DEBUG) Log.d(TAG, "lobby: " + method + " " + path + " failed: " + e);
            out.status = STATUS_NO_NETWORK;
        } finally {
            if (connection != null) connection.disconnect();
        }
        return out;
    }

    private static String readAll(InputStream in) {
        if (in == null) return null;
        try {
            ByteArrayOutputStream buffer = new ByteArrayOutputStream();
            byte[] chunk = new byte[4096];
            int read;
            while ((read = in.read(chunk)) > 0) {
                buffer.write(chunk, 0, read);
                if (buffer.size() > MAX_RESPONSE_BYTES) break;
            }
            return buffer.toString("UTF-8");
        } catch (Exception e) {
            return null;
        } finally {
            try {
                in.close();
            } catch (Exception e) {
                /* nothing useful to do */
            }
        }
    }

    private static String enc(String value) {
        try {
            return java.net.URLEncoder.encode(value != null ? value : "", "UTF-8");
        } catch (Exception e) {
            return "";
        }
    }

    /* A hand-typed server address in the prefs is one "https://" away from
     * failing as MalformedURLException, which our catch would report as "no
     * network" -- the wrong diagnosis entirely. Assume the scheme instead of
     * complaining about it. */
    private static String trimSlash(String base) {
        String url = (base == null || base.trim().length() == 0) ? DEFAULT_URL : base.trim();
        while (url.endsWith("/")) url = url.substring(0, url.length() - 1);
        if (!url.startsWith("http://") && !url.startsWith("https://"))
            url = "https://" + url;
        return url;
    }

    private static int clampSeconds(int value, int min, int max) {
        return value < min ? min : (value > max ? max : value);
    }

    private static String emptyToNull(String value) {
        return (value == null || value.length() == 0) ? null : value;
    }

    private static class Http {
        int status;
        int retryAfterSeconds;
        String body;
        String etag;
        String site;
        long dateSeconds;

        JSONObject json() {
            if (body == null || body.length() == 0) return null;
            try {
                return new JSONObject(body);
            } catch (Exception e) {
                if (DEBUG) Log.d(TAG, "lobby: unparsable answer");
                return null;
            }
        }
    }
}
