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

using System.Net;

namespace Mame4droid.Lobby.Model;

public enum RoomState
{
    Open,
    Claimed
}

/// NAT quality as reported by the peer's own STUN pass (pp = the NAT preserved
/// our source port, sym = the mapping changes per destination).  V6 = the peer
/// has an IPv6 tuple, which is a route with no NAT on it at all.
public sealed record NatInfo(bool Sym, bool Pp, bool Upnp, bool V6 = false);

/// One side's rendezvous data. Public/PublicAlt are STUN tuples kept verbatim:
/// the NAT may rewrite the port, so nothing here is ever normalised.
public sealed class PeerEntry
{
    public string? Public { get; init; }
    public string? PublicAlt { get; init; }
    public string[] Lan { get; init; } = Array.Empty<string>();
    public NatInfo Nat { get; init; } = new(false, false, false);
    public string? Country { get; init; }
    public bool Verified { get; init; }

    /* Source IP of the HTTP call. Used only to tell "same site" from a private
     * address collision, never logged and never sent to the other side. */
    public IPAddress? ObservedIp { get; init; }
}

/// A published game, alive only in memory. Mutating state/peer/expiry always
/// happens under Sync so two simultaneous joins cannot both win.
public sealed class Room
{
    public required string Id { get; init; }
    public required string Token { get; init; }
    public required string OwnerKey { get; init; }

    public required int Proto { get; init; }
    public required string App { get; init; }
    public required string Game { get; init; }
    public required int Mode { get; init; }
    public required int Delay { get; init; }
    public required bool Plugins { get; init; }
    public required PeerEntry Host { get; init; }

    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public DateTimeOffset ClaimedUntil { get; set; }

    public RoomState State { get; set; } = RoomState.Open;
    public PeerEntry? Peer { get; set; }

    /* Private room: the PIN itself is never stored, only its hash (see
     * Ids.HashPin). Null means anyone may join. */
    public string? PinHash { get; init; }

    /* Wrong PINs tried against this room. Four digits is 10,000 guesses, so
     * the per-caller rate limit alone would let a patient attacker through in
     * a few hours; a handful of misses closes the room to joins instead. */
    public int PinFailures { get; set; }

    /* Drop-in: the host is already playing. Whoever joins is lifted into the
     * running game with a state transfer rather than starting a new one, so
     * the board has to say which kind of room this is before anyone taps. */
    public required bool Playing { get; init; }

    /* Whatever the claimant called this attempt, or null when it was an older
     * build that sends none. Dies with the claim, so a lapsed one cannot be
     * used to take a room somebody else has since claimed. */
    public string? ClaimToken { get; set; }

    public bool IsLocked => PinHash is not null;

    public readonly object Sync = new();

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresUtc;
}
