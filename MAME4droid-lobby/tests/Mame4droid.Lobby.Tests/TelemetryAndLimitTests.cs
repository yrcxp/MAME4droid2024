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

public class TelemetryAndLimitTests
{
    [Fact]
    public async Task Telemetry_never_gives_a_reason_to_retry()
    {
        using var factory = new LobbyFactory();
        var client = factory.CallerFrom("88.1.2.3");

        var good = await client.PostJson("/api/v1/telemetry", new
        {
            proto = 11, app = "1.39.0", game = "mslug", role = "host", outcome = "connected",
            natSelf = new { sym = false, pp = true, upnp = true },
            natPeer = new { sym = true, pp = false, upnp = false },
            path = "punch", waitMs = 4200
        });
        Assert.Equal(HttpStatusCode.NoContent, good.StatusCode);

        var junk = await client.PostJson("/api/v1/telemetry", new
        {
            proto = 0, app = "?", game = "..", role = "spectator", outcome = "exploded",
            natSelf = (object?)null, natPeer = (object?)null, path = "carrier-pigeon", waitMs = -5
        });
        Assert.Equal(HttpStatusCode.NoContent, junk.StatusCode);
    }

    [Fact]
    public async Task Each_endpoint_has_its_own_budget_so_the_heartbeat_cannot_be_starved()
    {
        using var factory = new LobbyFactory(("RateLimits:Create", "2"), ("RateLimits:Poll", "1000"));
        var host = factory.CallerFrom("88.1.2.3");

        var first = await host.PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080"));
        var created = await first.Read<CreateResult>();
        await host.PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2081"));

        var throttled = await host.PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2082"));
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.NotNull(throttled.Headers.RetryAfter);

        /* Creating too often must not take the host's own poll down with it. */
        for (var beat = 0; beat < 10; beat++)
        {
            var poll = await host.PostJson($"/api/v1/rooms/{created.Id}/poll", new { token = created.Token });
            Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
        }
    }

    [Fact]
    public async Task Calling_one_endpoint_too_often_earns_a_429_with_a_backoff()
    {
        using var factory = new LobbyFactory(("RateLimits:Config", "1"));
        var client = factory.CallerFrom("88.1.2.3");

        var allowed = await client.GetAsync("/api/v1/config?proto=11&app=1.39.0");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        var refused = await client.GetAsync("/api/v1/config?proto=11&app=1.39.0");
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter);
    }

    [Fact]
    public async Task A_household_behind_one_router_is_not_throttled_out_of_the_board()
    {
        /* Pinned to what ships in appsettings.json: lifting them here is what
         * would make this test pass while the real thing still fails. */
        using var factory = new LobbyFactory(
            ("RateLimits:Config", "30"), ("RateLimits:Health", "60"));

        /* Field report: three devices on one LAN, and the third was stuck on
         * "waking the server up" for ever. The bucket is keyed by public
         * address, so the players were spending each other's budget -- and a
         * VPN on the third one "fixed" it, which is what gave it away. */
        var home = factory.CallerFrom("88.1.2.3");
        for (var device = 0; device < 6; device++)
        {
            var config = await home.GetAsync("/api/v1/config?proto=11&app=1.39.0");
            Assert.Equal(HttpStatusCode.OK, config.StatusCode);

            var wake = await home.GetAsync("/api/v1/health");
            Assert.Equal(HttpStatusCode.OK, wake.StatusCode);
        }
    }

    [Fact]
    public async Task Warming_the_instance_cannot_spend_the_board_s_own_budget()
    {
        using var factory = new LobbyFactory(("RateLimits:Health", "1"));
        var client = factory.CallerFrom("88.1.2.3");

        await client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await client.GetAsync("/api/v1/health")).StatusCode);

        /* Separate buckets: opening the netplay dialog a few times must never
         * be why the board itself cannot ask what the server wants. */
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync("/api/v1/config?proto=11&app=1.39.0")).StatusCode);
    }

    [Fact]
    public async Task Callers_are_told_apart_by_the_address_the_platform_reports()
    {
        using var factory = new LobbyFactory(("RateLimits:Create", "1"));

        var first = await factory.CallerFrom("88.1.2.3")
            .PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080"));
        var second = await factory.CallerFrom("77.4.5.6")
            .PostJson("/api/v1/rooms", RoomRequests.Create("77.4.5.6:2080"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task Forwarding_headers_from_the_caller_change_nothing()
    {
        using var factory = new LobbyFactory();
        var client = factory.CallerFrom("88.1.2.3");

        /* Regression: the host resolves the caller before the app runs, so
         * what is left in these headers by then is whatever the caller wrote.
         * Believing it let anyone publish a stranger's address as verified and
         * rotate past every rate limit. */
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "9.9.9.9");
        client.DefaultRequestHeaders.Add("X-Client-IP", "9.9.9.9");

        var spoofed = await client.PostJson("/api/v1/rooms", RoomRequests.Create("9.9.9.9:2080"));
        Assert.Equal(HttpStatusCode.BadRequest, spoofed.StatusCode);
        Assert.Equal("address_mismatch", (await spoofed.Read<ErrorResult>()).Error);

        var honest = await client.PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080"));
        Assert.Equal(HttpStatusCode.OK, honest.StatusCode);
        Assert.True((await honest.Read<CreateResult>()).Verified);
    }

    [Fact]
    public async Task A_spoofed_header_cannot_buy_a_fresh_rate_limit_bucket()
    {
        using var factory = new LobbyFactory(("RateLimits:Create", "1"));
        var client = factory.CallerFrom("88.1.2.3");

        Assert.Equal(HttpStatusCode.OK,
            (await client.PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080"))).StatusCode);

        client.DefaultRequestHeaders.Add("X-Forwarded-For", "9.9.9.9");
        var again = await client.PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2081"));

        Assert.Equal(HttpStatusCode.TooManyRequests, again.StatusCode);
    }

    [Fact]
    public async Task Health_answers_without_touching_the_store()
    {
        using var factory = new LobbyFactory();
        var response = await factory.CallerFrom("88.1.2.3").GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }
}
