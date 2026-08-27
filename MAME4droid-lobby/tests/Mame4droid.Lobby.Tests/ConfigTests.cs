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
using Xunit;

namespace Mame4droid.Lobby.Tests;

public class ConfigTests
{
    [Fact]
    public async Task Config_reports_cadences_and_backoff()
    {
        using var factory = new LobbyFactory();
        var client = factory.CallerFrom("88.1.2.3");

        var config = await (await client.GetAsync("/api/v1/config?proto=11&app=1.39.0")).Read<ConfigResult>();

        Assert.True(config.Enabled);
        Assert.Equal(3, config.PollSeconds);
        Assert.Equal(5, config.ListSeconds);
        Assert.Equal(20, config.ListMaxSeconds);
        Assert.False(config.UpdateAvailable);
    }

    [Fact]
    public async Task Old_build_is_told_to_update_but_never_refused()
    {
        using var factory = new LobbyFactory(("MinApp", "1.40.0"));
        var client = factory.CallerFrom("88.1.2.3");

        var config = await (await client.GetAsync("/api/v1/config?proto=11&app=1.39.0")).Read<ConfigResult>();
        Assert.True(config.UpdateAvailable);

        var created = await client.PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080"));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
    }

    [Fact]
    public async Task Whoami_stays_hidden_unless_diagnostics_are_switched_on()
    {
        using var off = new LobbyFactory();
        Assert.Equal(HttpStatusCode.NotFound,
            (await off.CallerFrom("88.1.2.3").GetAsync("/api/v1/whoami")).StatusCode);

        using var on = new LobbyFactory(("Diagnostics", "true"));
        var response = await on.CallerFrom("88.1.2.3").GetAsync("/api/v1/whoami");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("remoteIp=88.1.2.3", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Kill_switch_stops_everything_except_config()
    {
        using var factory = new LobbyFactory(("Enabled", "false"), ("Notice", "down for maintenance"));
        var client = factory.CallerFrom("88.1.2.3");

        var config = await (await client.GetAsync("/api/v1/config?proto=11&app=1.39.0")).Read<ConfigResult>();
        Assert.False(config.Enabled);
        Assert.Equal("down for maintenance", config.Notice);

        var rooms = await client.GetAsync("/api/v1/rooms?proto=11");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rooms.StatusCode);

        /* A board that is deliberately closed is not a sick instance, so a
         * health probe pointed here must not read the kill switch as an outage. */
        var health = await client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("ok", await health.Content.ReadAsStringAsync());

        var created = await client.PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, created.StatusCode);
    }
}
