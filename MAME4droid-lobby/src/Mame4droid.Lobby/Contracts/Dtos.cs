/*
 * This file is part of MAME4droid (NetPlay lobby server).
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
 */

namespace Mame4droid.Lobby.Contracts;

/// Wire shape of the NAT flags. Matches "pp=" / "sym=" from netplayGetPublicAddr()
/// plus the client's own UPnP mapping result.
/// Sym and Pp describe the IPv4 NAT, because that is the only family that
/// has one. V6 says the peer reached STUN over IPv6 and would dial that way:
/// two of those need no punching at all, whatever their v4 looks like.
/// Optional so a build that predates it still deserialises (false = unknown).
/// Mob says the reporter had only a carrier address: mobile data, with no
/// router to forward on. It separates "unreachable and fixable" from not.
public sealed record NatDto(bool Sym, bool Pp, bool Upnp, bool V6 = false, bool Mob = false);

public sealed record ConfigResponse(
    bool Enabled,
    int PollSeconds,
    int ListSeconds,
    double ListBackoff,
    int ListMaxSeconds,
    string MinApp,
    string Notice,
    bool UpdateAvailable,
    /* Rides here and nowhere else: /config is fetched once when the board opens,
     * so the showcase costs no request of its own. In the listing it would move
     * the ETag on every change and undo the 304s the traffic budget rests on. */
    StatsDto? Stats = null);

/// Recent activity, for a board with nothing on it right now. Every figure is
/// already past its threshold server-side, so a zero means "do not show this".
/// Flags is the busiest few ISO-3166 codes behind Countries, drawn as flags.
public sealed record StatsDto(
    string Since, int Rooms, int Played, string[] Games, int Countries, string[] Flags);

/// Public is the primary STUN tuple ("ip:port", or "[v6]:port" on a v6 host);
/// PublicAlt carries the "alt=" v4 tuple a dual-stack host also learned.
public sealed record CreateRoomRequest(
    int Proto,
    string? App,
    string? Game,
    int Mode,
    int Delay,
    bool Plugins,
    string? Public,
    string? PublicAlt,
    string[]? Lan,
    NatDto? Nat,
    string? Country,
    /* Optional PIN. Kept only as a hash and never listed: the board shows a
     * room is private, never anything about its PIN. */
    string? Pin = null,
    /* Drop-in: the host is already playing and whoever joins is brought up
     * to the running game with a state transfer, instead of both starting
     * from frame zero. Optional so an older build still creates rooms. */
    bool Playing = false);

public sealed record CreateRoomResponse(
    string Id,
    string Token,
    int Ttl,
    int PollSeconds,
    bool Verified);

/// No addresses here: the listing is public, so it carries only what is needed
/// to decide whether to join. Since is absolute on purpose -- a "seconds waiting"
/// field would change every second and defeat the ETag the quota depends on.
public sealed record RoomSummary(
    string Id,
    string Game,
    string? Country,
    int Mode,
    int Delay,
    bool Plugins,
    NatDto Nat,
    bool Verified,
    bool HasLan,
    string App,
    long Since,
    /* Salted hash of the host's address. Compared against the caller's own,
     * returned in the X-Lobby-Site header, it says "same router as you"
     * without disclosing anything -- and it keeps the body identical for
     * every viewer, which is what keeps the ETag working. */
    string Site,
    /* Private: joining needs the PIN the host set. Shown so a player knows
     * before tapping, and so nobody wastes a slot discovering it. */
    bool Locked = false,
    /* The host is playing right now and this is a drop-in: joining lands you
     * in a game already under way, not at the start of one. */
    bool Playing = false);

public sealed record RoomListResponse(IReadOnlyList<RoomSummary> Rooms, int Total);

public sealed record JoinRequest(
    int Proto,
    string? App,
    string? Public,
    string? PublicAlt,
    string[]? Lan,
    NatDto? Nat,
    string? Country,
    /* Required by a private room, ignored by an open one. */
    string? Pin = null,
    /* Names the joining attempt rather than the address it came from, which is
     * the only way to tell two phones behind one router apart when one retries.
     * Minted by the client and repeated on every try at the same room. Older
     * builds send none, and those are still judged by address. */
    string? Claim = null);

/// SameSite means both sides reached the lobby from the same public IP, so the
/// LAN addresses are the ones to use. It is the server-side equivalent of the
/// STUN probe resolveAndJoin() does today, and it costs the client nothing.
public sealed record PeerDto(
    string? Public,
    string? PublicAlt,
    string[] Lan,
    NatDto Nat,
    string? Country,
    bool Verified,
    bool SameSite);

public sealed record HostDto(
    string? Public,
    string? PublicAlt,
    string[] Lan,
    string Game,
    int Mode,
    int Delay,
    bool Plugins,
    NatDto Nat,
    string? Country,
    bool Verified,
    bool SameSite);

public sealed record JoinResponse(HostDto Host);

public sealed record PollRequest(string? Token);

/// One call does heartbeat and peer delivery. It stays idempotent: the peer is
/// returned on every poll until the host deletes the room or it expires, so a
/// lost response on a flaky mobile link does not kill the pairing.
public sealed record PollResponse(
    string State,
    int Ttl,
    int Viewers,
    PeerDto? Peer);

public sealed record TelemetryRequest(
    int Proto,
    string? App,
    string? Game,
    string? Role,
    string? Outcome,
    NatDto? NatSelf,
    NatDto? NatPeer,
    string? Path,
    int WaitMs,
    /* Who played with whom, as both sides declared it: the closest thing to a
     * distance measure the board has, and the answer to "is anyone out there
     * for me to play with". */
    string? Country = null,
    string? PeerCountry = null,
    /* Outcome correlates with these: rollback and a short delay are where
     * desyncs and disconnects show up first. */
    int Mode = 0,
    int Delay = 0,
    /* How long the session actually lasted. A pairing that connects and dies
     * in ten seconds counts as "connected" without this. */
    int PlayMs = 0,
    /* Round trip to the peer and its mean deviation, as the session measured
     * them. Whether a route is playable is a latency question, not a NAT one:
     * two players can pair perfectly and still be unplayable at 300 ms, and a
     * steady 120 ms beats a 60 ms that swings. */
    int RttMs = 0,
    int JitterMs = 0,
    /* Floor and ceiling seen during the session. The smoothed average weights
     * recent samples, so on its own it hides a link that was fine for ten
     * minutes and fell apart at the end. */
    int RttMinMs = 0,
    int RttMaxMs = 0,
    /* Whether the room was private. Worth knowing how people actually use the
     * board: friends arranging a match behind a PIN is a different service
     * from strangers meeting in the open. */
    bool Locked = false,
    /* The room both sides played in, so their reports can be read as one
     * match. Logged only as a salted hash: a raw id could be crossed with a
     * saved copy of the public board. */
    string? Room = null,
    /* Drop-in: the joiner was lifted into a game already running instead of
     * both starting one. A different shape of session -- rooms open for hours
     * rather than minutes, a state transfer rather than a boot -- so folding
     * it in with the rest would blur every rate it appears in. */
    bool DropIn = false);

public sealed record ErrorResponse(string Error, string? Notice = null);
