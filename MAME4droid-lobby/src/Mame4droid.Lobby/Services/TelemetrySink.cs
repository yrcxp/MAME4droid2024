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

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Mame4droid.Lobby.Contracts;

namespace Mame4droid.Lobby.Services;

/// Where the service earns its keep beyond matchmaking: it measures the real
/// success rate per NAT combination, which today is only a rule of thumb
/// ("punching works unless a side is symmetric"). No address, no stable id --
/// a line is only useful in aggregate.
public sealed class TelemetrySink
{
    /* "connected" says a pairing completed; "played" and "dropped" close it
     * afterwards with how long it lasted, which is what tells a working match
     * from a handshake that fell apart seconds later. "withdrawn" is the room
     * taking itself off the board -- the drop-in gate refusing a savestate the
     * rollback ring cannot hold -- as against "cancelled", which is a person. */
    private static readonly string[] Outcomes =
        { "connected", "played", "dropped", "timeout", "rom_missing", "build_mismatch",
          "cancelled", "withdrawn" };

    /* "v6" is its own path: a pair that dialled an IPv6 literal never punched
     * through anything, and counting those as punches flatters the rate. */
    private static readonly string[] Paths = { "lan", "punch", "forward", "upnp", "v6" };

    private static readonly string[] Roles = { "host", "client" };

    private readonly ILogger<TelemetrySink> _log;
    private readonly ConcurrentDictionary<string, long> _counters = new(StringComparer.Ordinal);

    private readonly StatsStore _stats;

    public TelemetrySink(ILogger<TelemetrySink> log, StatsStore stats)
    {
        _log = log;
        _stats = stats;
    }

    public bool Record(TelemetryRequest report)
    {
        var role = Pick(Roles, report.Role);
        var outcome = Pick(Outcomes, report.Outcome);
        var path = Pick(Paths, report.Path);

        if (role is null || outcome is null) return false;
        if (!RequestValidation.IsValidProto(report.Proto)) return false;

        var game = RequestValidation.IsValidGame(report.Game) ? report.Game! : "?";
        var app = RequestValidation.NormaliseApp(report.App);
        var self = Describe(report.NatSelf);
        var peer = Describe(report.NatPeer);
        var wait = Math.Clamp(report.WaitMs, 0, 30 * 60 * 1000);

        var here = RequestValidation.NormaliseCountry(report.Country) ?? "??";
        var there = RequestValidation.NormaliseCountry(report.PeerCountry) ?? "??";
        var mode = report.Mode == 1 ? "rollback" : "lockstep";
        var played = Math.Clamp(report.PlayMs, 0, 24 * 60 * 60 * 1000);

        var key = $"{outcome}|{path ?? "?"}|{self}|{peer}";
        _counters.AddOrUpdate(key, 1, static (_, current) => current + 1);

        /* Feeds the showcase. Host's line only, or both peers reporting the same
         * finished game would double it; real names only, so "?" never becomes a
         * game somebody is told to go and play. */
        if (outcome == "played" && role == "host" && game != "?")
            _stats.GamePlayed(game, RequestValidation.NormaliseCountry(report.Country));

        /* Second tally, by country pair: the one that answers whether a board
         * has enough people in reach of each other to be worth keeping up. */
        var route = $"{here}-{there}|{outcome}";
        _counters.AddOrUpdate(route, 1, static (_, current) => current + 1);

        /* Kept as its own tally rather than folded into the keys above: the
         * question is whether drop-in pairs as well as a normal start, and
         * splitting every existing counter in two would answer it by making
         * all of them smaller. */
        if (report.DropIn)
            _counters.AddOrUpdate($"dropin|{outcome}", 1, static (_, current) => current + 1);

        /* Latency only means something once a session ran, and a nonsense
         * value drags an average people read as advice. Its own key, with the
         * path in it: one country pair covers a LAN game at 8 ms and an
         * internet one at 90, and their average described neither. */
        /* Reported raw so the two questions stay separable: how often rollback
         * fired, and how much work each firing was. Per minute rather than per
         * session, or a long game always looks worse than a short one. */
        var misses = Math.Clamp(report.Rollbacks, 0, 10_000_000);
        var rewound = Math.Clamp(report.RollbackFrames, 0, 100_000_000);
        var perMin = played > 0 ? misses * 60000L / played : 0;
        var depth = misses > 0 ? rewound / misses : 0;

        var rtt = Math.Clamp(report.RttMs, 0, 10000);
        var jitter = Math.Clamp(report.JitterMs, 0, 10000);
        var floor = Math.Clamp(report.RttMinMs, 0, 10000);
        var ceiling = Math.Clamp(report.RttMaxMs, 0, 10000);
        var link = $"{here}-{there}|{path ?? "?"}|{outcome}";
        if (rtt > 0) TrackLatency(link, rtt, jitter, floor, ceiling);

        _log.LogInformation(
            "TELEM session={Session} proto={Proto} app={App} game={Game} role={Role} outcome={Outcome} " +
            "path={Path} self={Self} peer={Peer} route={Here}-{There} mode={Mode} " +
            "delay={Delay} waitMs={WaitMs} playMs={PlayMs} rttMs={Rtt} jitterMs={Jitter} " +
            "rangeMs={Floor}-{Ceiling} avgRttMs={AvgRtt} rbPerMin={PerMin} rbDepth={Depth} " +
            "room={Room} start={Start} total={Total}",
            SessionTag(report.Room), report.Proto, app, game, role, outcome, path ?? "?", self, peer,
            here, there, mode, Math.Clamp(report.Delay, 0, 20), wait, played,
            rtt, jitter, floor, ceiling, AverageRtt(link), perMin, depth,
            report.Locked ? "private" : "public",
            report.DropIn ? "dropin" : "together", _counters[key]);

        return true;
    }

    /// Running totals per outcome and NAT pair, for a quick look without pulling
    /// the log files down.
    public IReadOnlyDictionary<string, long> Counters => _counters;

    /// Mean round trip seen on one link -- a country pair over one path, so a
    /// LAN game and an internet one are never averaged together. Printed on
    /// every line so the log answers "is ES-AR over punch playable" by
    /// reading, without anyone adding up a month of numbers.
    public long AverageRtt(string link)
    {
        if (!_latency.TryGetValue(link, out var seen) || seen.Count == 0) return 0;
        return seen.TotalRtt / seen.Count;
    }

    /// Best and worst this route has ever shown, or zeros if never measured.
    /// The pair is what says whether an average is a fair description or the
    /// middle of two very different experiences.
    public (long Best, long Worst) RangeOf(string link)
        => _latency.TryGetValue(link, out var seen) ? (seen.Best, seen.Worst) : (0, 0);

    private void TrackLatency(string link, int rtt, int jitter, int floor, int ceiling)
    {
        /* A session that reported no floor still has its average as evidence,
         * so fall back to it rather than folding a zero into the best case. */
        var best = floor > 0 ? floor : rtt;
        var worst = ceiling > 0 ? ceiling : rtt;

        _latency.AddOrUpdate(link,
            _ => new Latency(1, rtt, jitter, best, worst),
            (_, current) => new Latency(current.Count + 1,
                current.TotalRtt + rtt, current.TotalJitter + jitter,
                Math.Min(current.Best, best), Math.Max(current.Worst, worst)));
    }

    private readonly ConcurrentDictionary<string, Latency> _latency = new(StringComparer.Ordinal);

    private readonly record struct Latency(
        long Count, long TotalRtt, long TotalJitter, long Best, long Worst);

    /// The whole point of the counters: which NAT pairs actually connect.
    /// v6 rides along because a pair that dialled over IPv6 never punched at
    /// all, and folding those in with the v4 attempts flatters the rate. mob
    /// separates a host nobody could reach because of its carrier from one
    /// whose router could have been opened.
    private static string Describe(NatDto? nat)
        => nat is null ? "?"
            : $"sym{(nat.Sym ? 1 : 0)}pp{(nat.Pp ? 1 : 0)}up{(nat.Upnp ? 1 : 0)}v6{(nat.V6 ? 1 : 0)}mob{(nat.Mob ? 1 : 0)}";

    /// Both peers of a match send the same room, so the same tag appears on
    /// every line of that game -- the join, and how each side saw it end.
    /// Salted per process: enough to group a log, useless for tying a game
    /// back to a room somebody saved off the public board.
    private string SessionTag(string? room)
    {
        if (string.IsNullOrEmpty(room) || room.Length > 16) return "-";
        return Convert.ToHexString(
            HMACSHA256.HashData(_sessionSalt, Encoding.UTF8.GetBytes(room)), 0, 4);
    }

    private readonly byte[] _sessionSalt = RandomNumberGenerator.GetBytes(16);

    private static string? Pick(string[] allowed, string? value)
        => value is not null && Array.Exists(allowed, x => string.Equals(x, value, StringComparison.Ordinal))
            ? value
            : null;
}
