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
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Mame4droid.Lobby.Configuration;
using Mame4droid.Lobby.Contracts;
using Mame4droid.Lobby.Model;

namespace Mame4droid.Lobby.Services;

public sealed record PeerValidation(string? Error, PeerEntry? Peer)
{
    public bool Ok => Error is null;
}

/// Every field that arrives from a device is checked here before it reaches the
/// store. Nothing is trusted, and nothing that fails is logged with the address
/// attached.
public static partial class RequestValidation
{
    public const int MaxProto = 1000;

    [GeneratedRegex("^[a-z0-9_]{1,32}$")]
    private static partial Regex GameNameRegex();

    [GeneratedRegex(@"^[0-9]{1,4}(\.[0-9]{1,4}){0,3}$")]
    private static partial Regex AppVersionRegex();

    [GeneratedRegex("^[A-Za-z]{2}$")]
    private static partial Regex CountryRegex();

    [GeneratedRegex("^[0-9]{4,8}$")]
    private static partial Regex PinRegex();

    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex ClaimRegex();

    /// Opaque to us: the client picks it and we only ever compare it with
    /// itself. Bounded and charset-checked because it is attacker-controlled
    /// and we keep it in memory for the life of a claim.
    public static bool IsValidClaim(string? claim)
        => !string.IsNullOrEmpty(claim) && ClaimRegex().IsMatch(claim);

    /// Digits only, four to eight. Numeric so it can be typed on a phone and
    /// read out loud without spelling anything, and long enough that guessing
    /// costs more than the room's own lifetime.
    public static bool IsValidPin(string? pin)
        => !string.IsNullOrEmpty(pin) && PinRegex().IsMatch(pin);

    /// driver.name as MAME writes it; anything else could not be a real game.
    public static bool IsValidGame(string? game)
        => !string.IsNullOrEmpty(game) && GameNameRegex().IsMatch(game);

    public static bool IsValidProto(int proto) => proto > 0 && proto <= MaxProto;

    public static string NormaliseApp(string? app)
        => !string.IsNullOrEmpty(app) && AppVersionRegex().IsMatch(app) ? app : "";

    /// Declared by the device from its SIM or locale, never derived from the IP:
    /// GeoIP would mean shipping a database, and this is a cosmetic flag.
    public static string? NormaliseCountry(string? country)
        => !string.IsNullOrEmpty(country) && CountryRegex().IsMatch(country)
            ? country.ToUpperInvariant()
            : null;

    /// Builds one side's rendezvous entry and runs the anti-reflection check,
    /// which is the only real abuse surface: without it anyone could publish a
    /// victim's address and have strangers aim UDP at it.
    public static PeerValidation ValidatePeer(
        string? advertised,
        string? advertisedAlt,
        string[]? lan,
        NatDto? nat,
        string? country,
        IPAddress observed,
        LobbyOptions options,
        bool requirePublic)
    {
        IPAddress? primary = null, alternate = null;

        if (!string.IsNullOrEmpty(advertised))
        {
            if (!NetTuple.TryParseWithPort(advertised, out var ip, out _) || !NetTuple.IsRoutable(ip))
                return new PeerValidation("bad_public", null);
            primary = ip;
        }
        else if (requirePublic)
        {
            return new PeerValidation("public_required", null);
        }

        if (!string.IsNullOrEmpty(advertisedAlt))
        {
            if (!NetTuple.TryParseWithPort(advertisedAlt, out var ip, out _)
                || ip.AddressFamily != AddressFamily.InterNetwork
                || !NetTuple.IsRoutable(ip))
                return new PeerValidation("bad_alt", null);
            alternate = ip;
        }

        /* A tuple in the same family as the HTTP connection must come from the
         * caller we are talking to; a different family (HTTP over v6, game
         * over v4) is legitimate on mobile and only costs the verified badge.
         * Same caller, not same address: a phone rotates through its /64, and
         * a /64 still belongs to one subscriber. */
        var comparable = false;
        var matched = false;

        foreach (var candidate in new[] { primary, alternate })
        {
            if (candidate is null || candidate.AddressFamily != observed.AddressFamily) continue;
            comparable = true;
            if (ClientAddress.SameSite(candidate, observed)) matched = true;
        }

        if (comparable && !matched && options.RejectUnverified)
            return new PeerValidation("address_mismatch", null);

        var peer = new PeerEntry
        {
            Public = advertised,
            PublicAlt = advertisedAlt,
            Lan = NormaliseLan(lan, options.MaxLanTuples),
            Nat = nat is null ? new NatInfo(false, false, false) : new NatInfo(nat.Sym, nat.Pp, nat.Upnp, nat.V6),
            Country = NormaliseCountry(country),
            Verified = matched,
            ObservedIp = observed
        };

        return new PeerValidation(null, peer);
    }

    /// Private v4 only, capped: a device with many interfaces should not be able
    /// to inflate the payload the other side has to try.
    private static string[] NormaliseLan(string[]? lan, int max)
    {
        if (lan is null || lan.Length == 0) return Array.Empty<string>();

        var kept = new List<string>(Math.Min(lan.Length, max));
        foreach (var entry in lan)
        {
            if (kept.Count >= max) break;
            if (!NetTuple.TryParse(entry, out var ip, out _)) continue;
            if (!NetTuple.IsPrivateV4(ip)) continue;
            if (!kept.Contains(entry, StringComparer.Ordinal)) kept.Add(entry);
        }
        return kept.ToArray();
    }
}
