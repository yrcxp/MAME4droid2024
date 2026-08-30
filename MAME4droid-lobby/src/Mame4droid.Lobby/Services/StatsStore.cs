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
using System.Globalization;
using System.Text;
using Mame4droid.Lobby.Configuration;
using Microsoft.Extensions.Options;

namespace Mame4droid.Lobby.Services;

/// What the board shows when nobody is hosting: how much has happened lately,
/// so an empty list reads as "you are early" instead of "this is dead".
public sealed record StatsSnapshot(
    string Since, int Rooms, int Played, IReadOnlyList<string> Games,
    int Countries, IReadOnlyList<string> Flags)
{
    public static readonly StatsSnapshot Empty =
        new("", 0, 0, Array.Empty<string>(), 0, Array.Empty<string>());

    /// Nothing cleared its threshold, so there is no showcase to draw.
    public bool Interesting => Rooms > 0 || Played > 0 || Games.Count > 0 || Countries > 0;
}

/// Day-bucketed counters behind that showcase, in a file rather than memory
/// alone: the instance unloads after twenty minutes of quiet, so "since the
/// server started" would read smallest exactly when the board is empty.
public sealed class StatsStore
{
    private readonly IOptionsMonitor<LobbyOptions> _options;
    private readonly ILogger<StatsStore> _log;
    private readonly string _path;

    private readonly ConcurrentDictionary<string, Day> _days = new(StringComparer.Ordinal);
    private int _dirty;
    private long _lastSaveTicks;
    private int _saving;

    /// One driver can be published under many names; past this a day stops
    /// learning new ones, so a flood of invented names cannot grow the file.
    private const int MaxGamesPerDay = 400;

    public StatsStore(IOptionsMonitor<LobbyOptions> options, IHostEnvironment env,
                      ILogger<StatsStore> log)
        : this(options, log, Path.Combine(
            LobbyLogging.LogDirectory(
                Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME"),
                Environment.GetEnvironmentVariable("HOME"),
                env.ContentRootPath),
            "stats.txt"))
    {
    }

    /// The same store against a chosen file, for tests and for a self-hosted
    /// instance that keeps its data somewhere of its own.
    public StatsStore(IOptionsMonitor<LobbyOptions> options, ILogger<StatsStore> log, string path)
    {
        _options = options;
        _log = log;
        _path = path;
        Load();

        /* Start the clock here, or the first room after a restart would write
         * back the file that was just read. */
        Interlocked.Exchange(ref _lastSaveTicks, DateTime.UtcNow.Ticks);
    }

    public void RoomCreated(string game, string? country)
    {
        var day = Today();
        Interlocked.Increment(ref day.Rooms);
        Note(day, game, country);
    }

    /// Counted from the host's line alone: both peers report a finished game,
    /// and counting both would double every session.
    public void GamePlayed(string game, string? country)
    {
        var day = Today();
        Interlocked.Increment(ref day.Played);
        Note(day, game, country);
    }

    /// What the board may show. Anything under its threshold comes back as
    /// nothing at all: "1 country" and "3 rooms" discourage rather than
    /// encourage, which is the opposite of why this exists.
    public StatsSnapshot Snapshot() => Collect(forBoard: true);

    /// The same window with nothing held back, for whoever runs the service:
    /// one country is a fact worth knowing, and games played is the figure most
    /// worth reading even while it is too raw to show a stranger.
    public StatsSnapshot Full() => Collect(forBoard: false);

    /// Every name the window holds and how often it came up, busiest first --
    /// what the file itself would tell you, only added up across its days.
    /// The board never sees this; it is for whoever runs the service.
    public (IReadOnlyList<KeyValuePair<string, int>> Games,
            IReadOnlyList<KeyValuePair<string, int>> Countries) Totals()
    {
        var window = Gather();
        return (Order(window.Games), Order(window.Countries));
    }

    private static KeyValuePair<string, int>[] Order(Dictionary<string, int> counts)
        => counts.OrderByDescending(x => x.Value)
                 .ThenBy(x => x.Key, StringComparer.Ordinal).ToArray();

    /// Adds the day buckets inside the window into one set of totals. Both the
    /// showcase and the operator page are cuts of this, so neither can drift
    /// into counting differently from the other.
    private Window Gather()
    {
        var days = Math.Max(1, _options.CurrentValue.StatsWindowDays);
        var first = DateTime.UtcNow.Date.AddDays(1 - days);

        var rooms = 0;
        var played = 0;
        var games = new Dictionary<string, int>(StringComparer.Ordinal);
        var countries = new Dictionary<string, int>(StringComparer.Ordinal);
        var since = DateTime.UtcNow.Date;

        foreach (var (key, day) in _days)
        {
            if (!DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date) || date < first)
                continue;

            if (date < since) since = date;
            rooms += Volatile.Read(ref day.Rooms);
            played += Volatile.Read(ref day.Played);
            foreach (var (name, count) in day.Games)
                games[name] = games.TryGetValue(name, out var had) ? had + count : count;
            foreach (var (iso, count) in day.Countries)
                countries[iso] = countries.TryGetValue(iso, out var seen) ? seen + count : count;
        }

        return new Window(
            since.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            rooms, played, games, countries);
    }

    private readonly record struct Window(
        string Since, int Rooms, int Played,
        Dictionary<string, int> Games, Dictionary<string, int> Countries);

    private StatsSnapshot Collect(bool forBoard)
    {
        var o = _options.CurrentValue;
        var (stamp, rooms, played, games, countries) = Gather();

        if (!forBoard)
            return new StatsSnapshot(stamp, rooms, played,
                Rank(games, o.StatsTopGames),
                countries.Count, Rank(countries, o.StatsTopCountries));

        var top = games.Count >= o.StatsMinGames ? Rank(games, o.StatsTopGames)
                                                 : Array.Empty<string>();

        /* The flags ride on the same threshold as the count they illustrate:
         * three of them next to "2 countries" would say more than the figure
         * does, and drawing every country turns a flourish into a wall. */
        var enough = countries.Count >= o.StatsMinCountries;

        return new StatsSnapshot(stamp,
            rooms >= o.StatsMinRooms ? rooms : 0,
            /* Withheld, not uncounted: the file keeps every one of these so the
             * figure is already there on the day it is worth showing. */
            o.StatsShowPlayed && played >= o.StatsMinPlayed ? played : 0,
            top,
            enough ? countries.Count : 0,
            enough ? Rank(countries, o.StatsTopCountries) : Array.Empty<string>());
    }

    /// Busiest first, ties broken by name so the same activity always produces
    /// the same list rather than one that reshuffles between polls.
    private static string[] Rank(Dictionary<string, int> counts, int take)
        => counts.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal)
                 .Take(Math.Max(1, take)).Select(x => x.Key).ToArray();

    private Day Today()
        => _days.GetOrAdd(DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                          _ => new Day());

    private void Note(Day day, string game, string? country)
    {
        if (!string.IsNullOrEmpty(game) && (day.Games.Count < MaxGamesPerDay
                                            || day.Games.ContainsKey(game)))
            day.Games.AddOrUpdate(game, 1, static (_, had) => had + 1);

        /* Counted rather than just listed: the board draws the busiest few as
         * flags, and a set could only ever offer them in alphabetical order. */
        if (!string.IsNullOrEmpty(country))
            day.Countries.AddOrUpdate(country, 1, static (_, had) => had + 1);

        Volatile.Write(ref _dirty, 1);
        SaveIfDue();
    }

    /// Write now instead of waiting for the next minute to come round. Called
    /// when the host is shutting down, which on a free instance is a routine
    /// event rather than a rare one.
    public void Flush() => Save(force: true);

    /// Debounced and off the request thread: these are a showcase, so losing the
    /// last few minutes to a recycle costs nothing worth making a caller wait
    /// for, let alone rewriting the file once per room.
    private void SaveIfDue()
    {
        var every = TimeSpan.FromSeconds(
            Math.Clamp(_options.CurrentValue.StatsSaveSeconds, 10, 3600)).Ticks;

        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastSaveTicks);
        if (now - last < every) return;
        if (Interlocked.CompareExchange(ref _lastSaveTicks, now, last) != last) return;
        if (Interlocked.CompareExchange(ref _saving, 1, 0) != 0) return;

        _ = Task.Run(() =>
        {
            try { Save(); }
            finally { Interlocked.Exchange(ref _saving, 0); }
        });
    }

    /// One line per day, pipe separated, so it can be read with an eye and a
    /// grep the way the telemetry log is.
    private void Save(bool force = false)
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0 && !force) return;

        /* A forced write can land while the once-a-minute one is still going,
         * and two of them into the same path truncate each other. */
        lock (_writing) WriteFile();
    }

    private void WriteFile()
    {
        var window = Math.Max(1, _options.CurrentValue.StatsWindowDays);
        var first = DateTime.UtcNow.Date.AddDays(1 - window);
        var text = new StringBuilder("# mame4droid lobby stats v1\n");

        foreach (var key in _days.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
                continue;

            /* Days that have fallen out of the window are dropped here rather
             * than swept: this is the only pass that walks them all. */
            if (date < first) { _days.TryRemove(key, out _); continue; }
            if (!_days.TryGetValue(key, out var day)) continue;

            text.Append(key)
                .Append("|rooms=").Append(Volatile.Read(ref day.Rooms))
                .Append("|played=").Append(Volatile.Read(ref day.Played))
                .Append("|games=")
                .Append(string.Join(',', day.Games.OrderByDescending(g => g.Value)
                                                  .Take(MaxGamesPerDay)
                                                  .Select(g => g.Key + ":" + g.Value)))
                .Append("|countries=")
                .Append(string.Join(',', day.Countries.OrderByDescending(c => c.Value)
                                                      .Select(c => c.Key + ":" + c.Value)))
                .Append('\n');
        }

        try
        {
            /* No BOM: this is meant to be read with a grep like the telemetry
             * log next to it, and one would sit in front of the first line. */
            File.WriteAllText(_path, text.ToString(), new UTF8Encoding(false));
        }
        catch (Exception e)
        {
            /* A showcase is not worth failing a request over. */
            _log.LogWarning("stats: could not write {Path}: {Message}", _path, e.Message);
        }
    }

    private readonly object _writing = new();

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            foreach (var line in File.ReadAllLines(_path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var parts = line.Split('|');
                if (parts.Length < 3) continue;

                var day = _days.GetOrAdd(parts[0], _ => new Day());
                foreach (var field in parts.Skip(1))
                {
                    var split = field.IndexOf('=');
                    if (split < 0) continue;
                    var name = field[..split];
                    var value = field[(split + 1)..];

                    switch (name)
                    {
                        case "rooms" when int.TryParse(value, out var rooms):
                            day.Rooms = rooms;
                            break;
                        case "played" when int.TryParse(value, out var played):
                            day.Played = played;
                            break;
                        case "games":
                            foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                            {
                                var colon = entry.LastIndexOf(':');
                                if (colon > 0 && int.TryParse(entry[(colon + 1)..], out var count))
                                    day.Games[entry[..colon]] = count;
                            }
                            break;
                        case "countries":
                            foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                            {
                                /* A file written before the counts existed
                                 * listed bare codes; those are worth one each
                                 * rather than worth discarding. */
                                var colon = entry.LastIndexOf(':');
                                if (colon < 0) { day.Countries[entry] = 1; continue; }
                                if (int.TryParse(entry[(colon + 1)..], out var seen))
                                    day.Countries[entry[..colon]] = seen;
                            }
                            break;
                    }
                }
            }

            _log.LogInformation("stats: loaded {Days} day(s) from {Path}", _days.Count, _path);
        }
        catch (Exception e)
        {
            /* Start from nothing rather than refuse to boot over a showcase. */
            _days.Clear();
            _log.LogWarning("stats: could not read {Path}: {Message}", _path, e.Message);
        }
    }

    private sealed class Day
    {
        public int Rooms;
        public int Played;
        public readonly ConcurrentDictionary<string, int> Games = new(StringComparer.Ordinal);
        public readonly ConcurrentDictionary<string, int> Countries = new(StringComparer.Ordinal);
    }
}
