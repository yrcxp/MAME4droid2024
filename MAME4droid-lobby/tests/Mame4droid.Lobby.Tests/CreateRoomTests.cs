using System.Net;
using Xunit;

namespace Mame4droid.Lobby.Tests;

public class CreateRoomTests
{
    [Fact]
    public async Task Advertising_the_address_we_are_talking_to_verifies_the_room()
    {
        using var factory = new LobbyFactory();
        var client = factory.CallerFrom("88.1.2.3");

        var response = await client.PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var room = await response.Read<CreateResult>();
        Assert.True(room.Verified);
        Assert.Equal(8, room.Id.Length);
        Assert.Equal(60, room.Ttl);
        Assert.NotEqual(room.Id, room.Token);
    }

    [Fact]
    public async Task Advertising_someone_elses_address_is_refused()
    {
        using var factory = new LobbyFactory();
        var client = factory.CallerFrom("88.1.2.3");

        /* The whole point of the check: otherwise a room could aim strangers'
         * UDP at a victim. */
        var response = await client.PostJson("/api/v1/rooms", RoomRequests.Create("9.9.9.9:2080"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("address_mismatch", (await response.Read<ErrorResult>()).Error);
    }

    [Fact]
    public async Task Mismatch_only_costs_the_badge_when_the_server_is_configured_to_forgive()
    {
        using var factory = new LobbyFactory(("RejectUnverified", "false"));
        var client = factory.CallerFrom("88.1.2.3");

        var room = await (await client.PostJson("/api/v1/rooms", RoomRequests.Create("9.9.9.9:2080")))
            .Read<CreateResult>();

        Assert.False(room.Verified);
    }

    [Fact]
    public async Task Http_over_v6_with_a_v4_game_socket_is_accepted_unverified()
    {
        using var factory = new LobbyFactory();
        var client = factory.CallerFrom("2a02:9000::1");

        /* Normal on mobile: the lobby call rides IPv6 while STUN reports the
         * carrier's CGNAT v4. Nothing is comparable, so nothing is refused. */
        var response = await client.PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await response.Read<CreateResult>()).Verified);
    }

    [Fact]
    public async Task Dual_stack_host_verifies_through_its_v4_alt()
    {
        using var factory = new LobbyFactory();
        var client = factory.CallerFrom("88.1.2.3");

        var response = await client.PostJson("/api/v1/rooms",
            RoomRequests.Create("[2a02:9000::1]:2080", publicAlt: "88.1.2.3:2080"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Read<CreateResult>()).Verified);
    }

    [Theory]
    [InlineData("Mslug")]
    [InlineData("mslug; rm -rf")]
    [InlineData("")]
    public async Task Only_a_real_driver_name_is_accepted(string game)
    {
        using var factory = new LobbyFactory();
        var client = factory.CallerFrom("88.1.2.3");

        var response = await client.PostJson("/api/v1/rooms",
            RoomRequests.Create("88.1.2.3:2080", game: game));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("bad_game", (await response.Read<ErrorResult>()).Error);
    }

    [Fact]
    public async Task A_private_address_cannot_be_published_as_public()
    {
        using var factory = new LobbyFactory();
        var client = factory.CallerFrom("88.1.2.3");

        var response = await client.PostJson("/api/v1/rooms", RoomRequests.Create("192.168.1.34:2080"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("bad_public", (await response.Read<ErrorResult>()).Error);
    }

    [Fact]
    public async Task A_public_tuple_without_a_port_is_useless_and_refused()
    {
        using var factory = new LobbyFactory();
        var client = factory.CallerFrom("88.1.2.3");

        var response = await client.PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("bad_public", (await response.Read<ErrorResult>()).Error);
    }

    [Fact]
    public async Task Lan_addresses_are_capped_and_filtered()
    {
        using var factory = new LobbyFactory(("MaxLanTuples", "2"));
        var client = factory.CallerFrom("88.1.2.3");

        var created = await (await client.PostJson("/api/v1/rooms", RoomRequests.Create(
                "88.1.2.3:2080",
                lan: new[] { "8.8.8.8:2080", "192.168.1.34:2080", "10.0.0.5:2080", "172.16.3.3:2080" })))
            .Read<CreateResult>();

        var joiner = factory.CallerFrom("77.4.5.6");
        var join = await (await joiner.PostJson($"/api/v1/rooms/{created.Id}/join",
            RoomRequests.Join("77.4.5.6:41320"))).Read<JoinResult>();

        Assert.Equal(new[] { "192.168.1.34:2080", "10.0.0.5:2080" }, join.Host.Lan);
    }

    [Fact]
    public async Task A_caller_cannot_flood_the_board_with_rooms()
    {
        /* Pinned low on purpose: the shipping value has to fit a houseful of
         * players, but the cap itself still has to bite somewhere. */
        using var factory = new LobbyFactory(("MaxRoomsPerIp", "2"));
        var client = factory.CallerFrom("88.1.2.3");

        await client.PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080"));
        await client.PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2081"));
        var third = await client.PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2082"));

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal("too_many_rooms", (await third.Read<ErrorResult>()).Error);
    }

    [Fact]
    public async Task Several_players_in_one_house_can_each_publish_a_game()
    {
        /* Field report: three phones on one LAN, and the third was refused
         * while the dialog said the server was waking up. The cap was 2 and
         * the address is what it counts, so two players used up the house.
         * The refusal even arrives as a 429, which is why it read as a rate
         * limit to everyone including me. */
        using var factory = new LobbyFactory();
        var home = factory.CallerFrom("88.1.2.3");

        for (var player = 0; player < 6; player++)
        {
            var room = await home.PostJson("/api/v1/rooms",
                RoomRequests.Create($"88.1.2.3:{2080 + player}"));
            Assert.Equal(HttpStatusCode.OK, room.StatusCode);
        }
    }

    [Fact]
    public async Task Oversized_bodies_never_reach_the_handler()
    {
        using var factory = new LobbyFactory();
        var client = factory.CallerFrom("88.1.2.3");

        var padded = new string('x', 2048);
        var response = await client.PostJson("/api/v1/rooms",
            RoomRequests.Create("88.1.2.3:2080", lan: new[] { padded }));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }
}
