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

using System.Globalization;
using Mame4droid.Lobby.Configuration;
using Mame4droid.Lobby.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mame4droid.Lobby.Tests;

/// The showcase an empty board draws. Its whole job is to encourage, so the
/// figures it refuses to show matter as much as the ones it does.
public class StatsTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "m4d-stats-" + Guid.NewGuid().ToString("N") + ".txt");

    private StatsStore Store(LobbyOptions? options = null)
        => new(new FixedOptions<LobbyOptions>(options ?? new LobbyOptions()),
               NullLogger<StatsStore>.Instance, _path);

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { }
    }

    [Fact]
    public void A_quiet_board_shows_nothing_rather_than_a_row_of_small_numbers()
    {
        var stats = Store();

        for (var i = 0; i < 4; i++) stats.RoomCreated("mslug", "ES");

        /* Four rooms is under every threshold, and "4 rooms, 1 country" reads
         * as a dead service -- which is the opposite of why this exists. */
        var quiet = stats.Snapshot();
        Assert.False(quiet.Interesting);
        Assert.Equal(0, quiet.Rooms);
        Assert.Empty(quiet.Flags);
    }

    [Fact]
    public void Past_the_threshold_the_busiest_games_come_back_in_order()
    {
        var stats = Store();

        for (var i = 0; i < 12; i++) stats.RoomCreated("dino", "ES");
        for (var i = 0; i < 5; i++) stats.RoomCreated("mslug", "ES");
        for (var i = 0; i < 2; i++) stats.RoomCreated("sf2", "ES");
        stats.RoomCreated("kinst", "ES");

        var seen = stats.Snapshot();
        Assert.True(seen.Interesting);
        Assert.Equal(20, seen.Rooms);

        /* Busiest first, and only as many as the board has room to suggest. */
        Assert.Equal(new[] { "dino", "mslug", "sf2" }, seen.Games);
    }

    [Fact]
    public void Games_stay_hidden_until_there_are_enough_of_them_to_rank()
    {
        var stats = Store();

        /* Twenty rooms of one game is a busy board, but "most played: dino"
         * off a single name is not a ranking, it is a restatement. */
        for (var i = 0; i < 20; i++) stats.RoomCreated("dino", "ES");

        var seen = stats.Snapshot();
        Assert.Equal(20, seen.Rooms);
        Assert.Empty(seen.Games);
    }

    [Fact]
    public void The_flags_are_the_busiest_countries_and_follow_their_own_count()
    {
        var stats = Store();

        for (var i = 0; i < 3; i++) stats.RoomCreated("dino", "AR");
        for (var i = 0; i < 9; i++) stats.RoomCreated("mslug", "ES");
        for (var i = 0; i < 5; i++) stats.RoomCreated("sf2", "BR");
        stats.RoomCreated("kinst", "JP");

        /* Four countries is under the threshold, so neither the number nor the
         * flags that illustrate it are shown. */
        Assert.Equal(0, stats.Snapshot().Countries);
        Assert.Empty(stats.Snapshot().Flags);

        stats.RoomCreated("dino", "US");

        var seen = stats.Snapshot();
        Assert.Equal(5, seen.Countries);
        Assert.Equal(new[] { "ES", "BR", "AR" }, seen.Flags);
    }

    [Fact]
    public void A_finished_game_counts_separately_from_the_room_that_held_it()
    {
        var stats = Store(new LobbyOptions { StatsShowPlayed = true });

        for (var i = 0; i < 10; i++) stats.RoomCreated("dino", "ES");
        for (var i = 0; i < 3; i++) stats.GamePlayed("dino", "ES");

        var seen = stats.Snapshot();

        /* Rooms opened and games actually played are different claims, and the
         * second is the one worth making. */
        Assert.Equal(10, seen.Rooms);
        Assert.Equal(3, seen.Played);
    }

    [Fact]
    public void Games_played_are_counted_but_withheld_until_they_are_turned_on()
    {
        var stats = Store();

        for (var i = 0; i < 10; i++) stats.RoomCreated("dino", "ES");
        for (var i = 0; i < 5; i++) stats.GamePlayed("dino", "ES");

        /* Next to the rooms figure this currently reads as a gulf: a room is
         * counted when it opens, a game only once two people finished one. */
        Assert.Equal(0, stats.Snapshot().Played);
        stats.Flush();

        /* Withheld, not uncounted -- the day it is worth showing, the history
         * is already there rather than starting from that day. */
        var shown = new StatsStore(
            new FixedOptions<LobbyOptions>(new LobbyOptions { StatsShowPlayed = true }),
            NullLogger<StatsStore>.Instance, _path);
        Assert.Equal(5, shown.Snapshot().Played);
    }

    [Fact]
    public void Days_outside_the_window_stop_counting()
    {
        var narrow = new LobbyOptions { StatsWindowDays = 1, StatsMinRooms = 1 };

        var old = DateTime.UtcNow.Date.AddDays(-40)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        File.WriteAllText(_path,
            "# mame4droid lobby stats v1\n" +
            old + "|rooms=500|played=200|games=dino:500|countries=ES:500\n");

        var stats = Store(narrow);
        for (var i = 0; i < 2; i++) stats.RoomCreated("mslug", "ES");

        /* A rolling window, not a running total: five hundred rooms from last
         * month describe a board that is not the one being looked at. */
        Assert.Equal(2, stats.Snapshot().Rooms);
    }

    [Fact]
    public void Counts_survive_the_restart_the_free_instance_makes_routine()
    {
        var first = Store(new LobbyOptions { StatsShowPlayed = true });
        for (var i = 0; i < 11; i++) first.RoomCreated("dino", "ES");
        for (var i = 0; i < 5; i++) first.RoomCreated("mslug", "AR");
        first.RoomCreated("sf2", "BR");
        for (var i = 0; i < 4; i++) first.GamePlayed("dino", "ES");
        first.Flush();

        /* The whole reason this is a file: an idle unload must not reset the
         * numbers to zero exactly when an empty board needs them most. */
        var reloaded = Store(new LobbyOptions { StatsShowPlayed = true }).Snapshot();
        Assert.Equal(17, reloaded.Rooms);
        Assert.Equal(4, reloaded.Played);
        Assert.Equal("dino", reloaded.Games[0]);
    }

    [Fact]
    public void A_file_written_before_countries_were_counted_still_loads()
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        File.WriteAllText(_path,
            "# mame4droid lobby stats v1\n" +
            today + "|rooms=20|played=9|games=dino:20|countries=ES,AR,BR,JP,US\n");

        var seen = Store().Snapshot();

        /* Bare codes were worth one appearance each; discarding them would
         * throw away the history on the first deploy that reads them. */
        Assert.Equal(20, seen.Rooms);
        Assert.Equal(5, seen.Countries);
        Assert.Equal(3, seen.Flags.Count);
    }
}
