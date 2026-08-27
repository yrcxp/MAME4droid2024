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
using System.Net.Http.Headers;
using Xunit;

namespace Mame4droid.Lobby.Tests;

public class ListingTests
{
    [Fact]
    public async Task Listing_carries_no_addresses()
    {
        using var factory = new LobbyFactory();
        var host = factory.CallerFrom("88.1.2.3");
        await host.PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080"));

        var response = await factory.CallerFrom("77.4.5.6").GetAsync("/api/v1/rooms?proto=11");
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("88.1.2.3", body);
        Assert.DoesNotContain("192.168.1.34", body);

        var list = System.Text.Json.JsonSerializer.Deserialize<ListResult>(body, LobbyFactory.Json)!;
        var room = Assert.Single(list.Rooms);
        Assert.Equal("mslug", room.Game);
        Assert.Equal("ES", room.Country);
        Assert.True(room.HasLan);
        Assert.True(room.Verified);
        Assert.True(room.Since > 0);
    }

    [Fact]
    public async Task Unchanged_board_answers_304_and_the_client_pays_only_headers()
    {
        using var factory = new LobbyFactory();
        var host = factory.CallerFrom("88.1.2.3");
        await host.PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080"));

        var viewer = factory.CallerFrom("77.4.5.6");
        var first = await viewer.GetAsync("/api/v1/rooms?proto=11");
        var etag = first.Headers.ETag!.ToString();

        viewer.DefaultRequestHeaders.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));
        var second = await viewer.GetAsync("/api/v1/rooms?proto=11");

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());

        /* A second request a moment later must still match: nothing in the body
         * is allowed to tick with the clock, or the quota plan collapses. */
        var third = await viewer.GetAsync("/api/v1/rooms?proto=11");
        Assert.Equal(HttpStatusCode.NotModified, third.StatusCode);
    }

    [Fact]
    public async Task A_new_room_moves_the_etag()
    {
        using var factory = new LobbyFactory();
        var viewer = factory.CallerFrom("77.4.5.6");

        var before = await viewer.GetAsync("/api/v1/rooms?proto=11");
        var etag = before.Headers.ETag!.ToString();

        await factory.CallerFrom("88.1.2.3").PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080"));

        viewer.DefaultRequestHeaders.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));
        var after = await viewer.GetAsync("/api/v1/rooms?proto=11");

        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.NotEqual(etag, after.Headers.ETag!.ToString());
        Assert.Single((await after.Read<ListResult>()).Rooms);
    }


    [Fact]
    public async Task A_drop_in_room_says_so_on_the_board()
    {
        using var factory = new LobbyFactory();

        await factory.CallerFrom("88.1.2.3")
            .PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080", playing: true));
        await factory.CallerFrom("77.4.5.6")
            .PostJson("/api/v1/rooms", RoomRequests.Create("77.4.5.6:2080", game: "sf2"));

        var list = await (await factory.CallerFrom("9.9.9.9")
            .GetAsync("/api/v1/rooms?proto=11")).Read<ListResult>();

        /* Joining a game already under way is a different proposition from
         * waiting at the start of one: the board has to say which is which
         * before anyone taps, not after. */
        Assert.True(list.Rooms.Single(r => r.Game == "mslug").Playing);
        Assert.False(list.Rooms.Single(r => r.Game == "sf2").Playing);
    }

    [Fact]
    public async Task A_room_from_a_build_that_predates_drop_in_is_a_normal_one()
    {
        using var factory = new LobbyFactory();

        /* No "playing" in the body at all, the way an older APK creates it. */
        await factory.CallerFrom("88.1.2.3").PostJson("/api/v1/rooms", new
        {
            proto = 11, app = "1.38.0", game = "mslug", mode = 1, delay = 0,
            plugins = false, @public = "88.1.2.3:2080",
            lan = new[] { "192.168.1.34:2080" },
            nat = new { sym = false, pp = true, upnp = false }, country = "ES"
        });

        var list = await (await factory.CallerFrom("9.9.9.9")
            .GetAsync("/api/v1/rooms?proto=11")).Read<ListResult>();

        Assert.False(Assert.Single(list.Rooms).Playing);
    }
    [Fact]
    public async Task Rooms_of_another_protocol_are_not_even_shown()
    {
        using var factory = new LobbyFactory();
        await factory.CallerFrom("88.1.2.3")
            .PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080", proto: 12));

        var list = await (await factory.CallerFrom("77.4.5.6").GetAsync("/api/v1/rooms?proto=11"))
            .Read<ListResult>();

        Assert.Empty(list.Rooms);
    }

    [Fact]
    public async Task Etags_do_not_leak_between_protocols()
    {
        using var factory = new LobbyFactory();
        var viewer = factory.CallerFrom("77.4.5.6");

        var eleven = await viewer.GetAsync("/api/v1/rooms?proto=11");
        var twelve = await viewer.GetAsync("/api/v1/rooms?proto=12");

        Assert.NotEqual(eleven.Headers.ETag!.ToString(), twelve.Headers.ETag!.ToString());
    }

    [Fact]
    public async Task A_claimed_room_leaves_the_board()
    {
        using var factory = new LobbyFactory();
        var created = await (await factory.CallerFrom("88.1.2.3")
            .PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080"))).Read<CreateResult>();

        await factory.CallerFrom("77.4.5.6")
            .PostJson($"/api/v1/rooms/{created.Id}/join", RoomRequests.Join("77.4.5.6:41320"));

        var list = await (await factory.CallerFrom("12.13.14.15").GetAsync("/api/v1/rooms?proto=11"))
            .Read<ListResult>();

        Assert.Empty(list.Rooms);
    }

    [Fact]
    public async Task A_viewer_can_spot_the_rooms_behind_its_own_router()
    {
        using var factory = new LobbyFactory();
        await factory.CallerFrom("88.1.2.3")
            .PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080"));
        await factory.CallerFrom("77.4.5.6")
            .PostJson("/api/v1/rooms", RoomRequests.Create("77.4.5.6:2080", game: "sf2"));

        var viewer = factory.CallerFrom("88.1.2.3");
        var response = await viewer.GetAsync("/api/v1/rooms?proto=11");
        var mine = response.Headers.GetValues("X-Lobby-Site").Single();
        var list = await response.Read<ListResult>();

        /* Same public address means one router, so the LAN addresses are the
         * ones that will work -- and the board can say so before anyone joins. */
        Assert.Equal(mine, list.Rooms.Single(r => r.Game == "mslug").Site);
        Assert.NotEqual(mine, list.Rooms.Single(r => r.Game == "sf2").Site);

        /* No address anywhere in it: the hash is all a viewer ever sees. */
        var body = await (await viewer.GetAsync("/api/v1/rooms?proto=11")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("88.1.2.3", body);
        Assert.DoesNotContain("77.4.5.6", body);
    }

    [Fact]
    public async Task The_site_header_still_arrives_with_a_304()
    {
        using var factory = new LobbyFactory();
        await factory.CallerFrom("88.1.2.3")
            .PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080"));

        var viewer = factory.CallerFrom("88.1.2.3");
        var first = await viewer.GetAsync("/api/v1/rooms?proto=11");
        viewer.DefaultRequestHeaders.IfNoneMatch.Add(
            EntityTagHeaderValue.Parse(first.Headers.ETag!.ToString()));

        var second = await viewer.GetAsync("/api/v1/rooms?proto=11");

        /* Otherwise a viewer that is up to date would lose the one thing it
         * needs to recognise its own network. */
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Equal(first.Headers.GetValues("X-Lobby-Site").Single(),
            second.Headers.GetValues("X-Lobby-Site").Single());
    }

    [Fact]
    public async Task Two_viewers_get_the_same_body_so_the_etag_still_holds()
    {
        using var factory = new LobbyFactory();
        await factory.CallerFrom("88.1.2.3")
            .PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080"));

        var here = await factory.CallerFrom("88.1.2.3").GetAsync("/api/v1/rooms?proto=11");
        var there = await factory.CallerFrom("12.13.14.15").GetAsync("/api/v1/rooms?proto=11");

        /* The site hash lives in the body but describes the ROOM, not the
         * viewer: making it per-viewer would give everyone a private ETag and
         * undo the 304s the traffic budget rests on. */
        Assert.Equal(here.Headers.ETag!.ToString(), there.Headers.ETag!.ToString());
        Assert.Equal(await here.Content.ReadAsStringAsync(),
            await there.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Listing_needs_a_protocol()
    {
        using var factory = new LobbyFactory();
        var response = await factory.CallerFrom("77.4.5.6").GetAsync("/api/v1/rooms");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
