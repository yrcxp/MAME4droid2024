using System.Net;
using Xunit;

namespace Mame4droid.Lobby.Tests;

public class PairingTests
{
    [Fact]
    public async Task Both_sides_learn_about_each_other()
    {
        using var factory = new LobbyFactory();
        var host = factory.CallerFrom("88.1.2.3");
        var client = factory.CallerFrom("77.4.5.6");

        var created = await (await host.PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080")))
            .Read<CreateResult>();

        var join = await (await client.PostJson($"/api/v1/rooms/{created.Id}/join",
            RoomRequests.Join("77.4.5.6:41320"))).Read<JoinResult>();

        Assert.Equal("88.1.2.3:2080", join.Host.Public);
        Assert.Equal("mslug", join.Host.Game);
        Assert.Equal(1, join.Host.Mode);
        Assert.False(join.Host.SameSite);

        /* Punching is symmetric: without this half the host could never aim its
         * punch back and every NAT but full cone would fail. */
        var poll = await (await host.PostJson($"/api/v1/rooms/{created.Id}/poll",
            new { token = created.Token })).Read<PollResult>();

        Assert.Equal("claimed", poll.State);
        Assert.Equal("77.4.5.6:41320", poll.Peer!.Public);
        Assert.Equal(new[] { "192.168.1.50:2080" }, poll.Peer.Lan);
        Assert.True(poll.Peer.Nat.Sym);
        Assert.Equal("AR", poll.Peer.Country);
    }

    [Fact]
    public async Task Same_public_address_on_both_sides_is_flagged_as_one_site()
    {
        using var factory = new LobbyFactory();
        var host = factory.CallerFrom("88.1.2.3");
        var client = factory.CallerFrom("88.1.2.3");

        var created = await (await host.PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080")))
            .Read<CreateResult>();

        var join = await (await client.PostJson($"/api/v1/rooms/{created.Id}/join",
            RoomRequests.Join("88.1.2.3:41320"))).Read<JoinResult>();

        /* Same router: the LAN addresses are the ones to dial, which is exactly
         * what resolveAndJoin works out today with an extra STUN probe. */
        Assert.True(join.Host.SameSite);

        var poll = await (await host.PostJson($"/api/v1/rooms/{created.Id}/poll",
            new { token = created.Token })).Read<PollResult>();

        Assert.True(poll.Peer!.SameSite);
    }

    [Fact]
    public async Task Two_devices_in_one_house_are_one_site_over_ipv6_too()
    {
        using var factory = new LobbyFactory();
        var host = factory.CallerFrom("2a02:9000:1234:5678::a1");
        var client = factory.CallerFrom("2a02:9000:1234:5678::f9");

        var created = await (await host.PostJson("/api/v1/rooms",
            RoomRequests.Create("[2a02:9000:1234:5678::a1]:2080"))).Read<CreateResult>();

        var join = await (await client.PostJson($"/api/v1/rooms/{created.Id}/join",
            RoomRequests.Join("[2a02:9000:1234:5678::f9]:2080"))).Read<JoinResult>();

        /* Over IPv6 two phones on one Wi-Fi get different addresses in the
         * same /64. Comparing exactly would call them strangers and send them
         * out to the internet to reach the room next door. */
        Assert.True(join.Host.SameSite);
    }

    [Fact]
    public async Task Different_prefixes_are_not_the_same_house()
    {
        using var factory = new LobbyFactory();
        var created = await (await factory.CallerFrom("2a02:9000:1234:5678::a1")
            .PostJson("/api/v1/rooms", RoomRequests.Create("[2a02:9000:1234:5678::a1]:2080")))
            .Read<CreateResult>();

        var join = await (await factory.CallerFrom("2a02:9000:9999:0000::f9")
            .PostJson($"/api/v1/rooms/{created.Id}/join",
                RoomRequests.Join("[2a02:9000:9999:0000::f9]:2080"))).Read<JoinResult>();

        Assert.False(join.Host.SameSite);
    }

    [Fact]
    public async Task A_client_can_correct_the_tuple_it_guessed_at_when_joining()
    {
        using var factory = new LobbyFactory();
        var host = factory.CallerFrom("88.1.2.3");
        var client = factory.CallerFrom("77.4.5.6");

        var created = await (await host.PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080")))
            .Read<CreateResult>();
        await client.PostJson($"/api/v1/rooms/{created.Id}/join", RoomRequests.Join(null));

        var fixup = await client.PostJson($"/api/v1/rooms/{created.Id}/peer",
            RoomRequests.Join("77.4.5.6:52413"));
        Assert.Equal(HttpStatusCode.NoContent, fixup.StatusCode);

        /* The host aims at what STUN actually reported, not at the guess the
         * client had to make before it had a socket. */
        var poll = await (await host.PostJson($"/api/v1/rooms/{created.Id}/poll",
            new { token = created.Token })).Read<PollResult>();
        Assert.Equal("77.4.5.6:52413", poll.Peer!.Public);
    }

    [Fact]
    public async Task Only_whoever_claimed_the_room_may_correct_its_tuple()
    {
        using var factory = new LobbyFactory();
        var created = await (await factory.CallerFrom("88.1.2.3")
            .PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080"))).Read<CreateResult>();

        await factory.CallerFrom("77.4.5.6")
            .PostJson($"/api/v1/rooms/{created.Id}/join", RoomRequests.Join("77.4.5.6:41320"));

        /* There is no token on this side, so the claiming address is the only
         * credential: without this check anyone could redirect the punch. */
        var intruder = await factory.CallerFrom("12.13.14.15")
            .PostJson($"/api/v1/rooms/{created.Id}/peer", RoomRequests.Join("12.13.14.15:41320"));

        Assert.Equal(HttpStatusCode.Forbidden, intruder.StatusCode);
    }

    [Fact]
    public async Task An_unclaimed_room_has_no_tuple_to_correct()
    {
        using var factory = new LobbyFactory();
        var created = await (await factory.CallerFrom("88.1.2.3")
            .PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080"))).Read<CreateResult>();

        var response = await factory.CallerFrom("77.4.5.6")
            .PostJson($"/api/v1/rooms/{created.Id}/peer", RoomRequests.Join("77.4.5.6:41320"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_mobile_host_is_not_refused_for_rotating_its_ipv6()
    {
        using var factory = new LobbyFactory();

        /* Privacy extensions hand a phone several addresses in its /64, so the
         * game socket and this very HTTP call routinely differ. Demanding an
         * exact match refused honest hosts on mobile. */
        var response = await factory.CallerFrom("2a02:9000:1234:5678::c0de")
            .PostJson("/api/v1/rooms", RoomRequests.Create("[2a02:9000:1234:5678::beef]:2080"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Read<CreateResult>()).Verified);
    }

    [Fact]
    public async Task Another_subscribers_prefix_is_still_refused()
    {
        using var factory = new LobbyFactory();
        var response = await factory.CallerFrom("2a02:9000:1234:5678::c0de")
            .PostJson("/api/v1/rooms", RoomRequests.Create("[2a02:9000:9999:0000::beef]:2080"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_client_that_has_not_run_stun_yet_can_still_join()
    {
        using var factory = new LobbyFactory();
        var created = await (await factory.CallerFrom("88.1.2.3")
            .PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080"))).Read<CreateResult>();

        var response = await factory.CallerFrom("77.4.5.6")
            .PostJson($"/api/v1/rooms/{created.Id}/join", RoomRequests.Join(null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_private_room_says_it_is_private_and_nothing_else()
    {
        using var factory = new LobbyFactory();
        await factory.CallerFrom("88.1.2.3")
            .PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080", pin: "4213"));

        var response = await factory.CallerFrom("77.4.5.6").GetAsync("/api/v1/rooms?proto=11");
        var body = await response.Content.ReadAsStringAsync();

        /* The board says a room needs a PIN so nobody wastes a try finding
         * out; the PIN itself never leaves the device that set it. */
        Assert.True((await response.Read<ListResult>()).Rooms.Single().Locked);
        Assert.DoesNotContain("4213", body);
    }

    [Fact]
    public async Task The_right_pin_gets_in_and_a_wrong_one_does_not()
    {
        using var factory = new LobbyFactory();
        var created = await (await factory.CallerFrom("88.1.2.3")
            .PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080", pin: "4213")))
            .Read<CreateResult>();

        var wrong = await factory.CallerFrom("77.4.5.6")
            .PostJson($"/api/v1/rooms/{created.Id}/join", RoomRequests.Join("77.4.5.6:41320", pin: "1111"));
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal("bad_pin", (await wrong.Read<ErrorResult>()).Error);

        var none = await factory.CallerFrom("77.4.5.6")
            .PostJson($"/api/v1/rooms/{created.Id}/join", RoomRequests.Join("77.4.5.6:41320"));
        Assert.Equal(HttpStatusCode.Forbidden, none.StatusCode);

        var right = await factory.CallerFrom("77.4.5.6")
            .PostJson($"/api/v1/rooms/{created.Id}/join", RoomRequests.Join("77.4.5.6:41320", pin: "4213"));
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    [Fact]
    public async Task A_room_stops_answering_once_the_guessing_starts()
    {
        using var factory = new LobbyFactory(("MaxPinAttempts", "3"));
        var created = await (await factory.CallerFrom("88.1.2.3")
            .PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080", pin: "4213")))
            .Read<CreateResult>();

        var guesser = factory.CallerFrom("77.4.5.6");
        for (var attempt = 0; attempt < 3; attempt++)
            await guesser.PostJson($"/api/v1/rooms/{created.Id}/join",
                RoomRequests.Join("77.4.5.6:41320", pin: "0000"));

        /* Four digits is 10,000 guesses: spread thin enough, a per-caller rate
         * limit would let a patient attacker through, so the room itself gives
         * up -- even on the correct PIN. */
        var right = await guesser.PostJson($"/api/v1/rooms/{created.Id}/join",
            RoomRequests.Join("77.4.5.6:41320", pin: "4213"));

        Assert.Equal(HttpStatusCode.Locked, right.StatusCode);
        Assert.Equal("pin_blocked", (await right.Read<ErrorResult>()).Error);
    }

    [Fact]
    public async Task An_open_room_ignores_a_pin_nobody_asked_for()
    {
        using var factory = new LobbyFactory();
        var created = await (await factory.CallerFrom("88.1.2.3")
            .PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080"))).Read<CreateResult>();

        var response = await factory.CallerFrom("77.4.5.6")
            .PostJson($"/api/v1/rooms/{created.Id}/join", RoomRequests.Join("77.4.5.6:41320", pin: "9999"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("123456789")]
    [InlineData("12a4")]
    public async Task A_pin_that_is_not_four_to_eight_digits_is_refused(string pin)
    {
        using var factory = new LobbyFactory();

        /* Publishing a room the host believes is private would be worse than
         * an error, so a malformed PIN is never quietly dropped. */
        var response = await factory.CallerFrom("88.1.2.3")
            .PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080", pin: pin));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("bad_pin", (await response.Read<ErrorResult>()).Error);
    }

    [Fact]
    public async Task Second_client_to_a_room_gets_a_clean_conflict()
    {
        using var factory = new LobbyFactory();
        var created = await (await factory.CallerFrom("88.1.2.3")
            .PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080"))).Read<CreateResult>();

        await factory.CallerFrom("77.4.5.6")
            .PostJson($"/api/v1/rooms/{created.Id}/join", RoomRequests.Join("77.4.5.6:41320"));

        var second = await factory.CallerFrom("12.13.14.15")
            .PostJson($"/api/v1/rooms/{created.Id}/join", RoomRequests.Join("12.13.14.15:41320"));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("already_claimed", (await second.Read<ErrorResult>()).Error);
    }

    [Fact]
    public async Task Simultaneous_joins_produce_exactly_one_winner()
    {
        using var factory = new LobbyFactory();
        var created = await (await factory.CallerFrom("88.1.2.3")
            .PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080"))).Read<CreateResult>();

        var racers = Enumerable.Range(0, 8).Select(i =>
        {
            var caller = factory.CallerFrom($"77.4.5.{i + 10}");
            return caller.PostJson($"/api/v1/rooms/{created.Id}/join",
                RoomRequests.Join($"77.4.5.{i + 10}:41320"));
        }).ToArray();

        var results = await Task.WhenAll(racers);

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(7, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task A_different_protocol_is_refused_before_the_peers_ever_talk()
    {
        using var factory = new LobbyFactory();
        var created = await (await factory.CallerFrom("88.1.2.3")
            .PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080"))).Read<CreateResult>();

        var response = await factory.CallerFrom("77.4.5.6")
            .PostJson($"/api/v1/rooms/{created.Id}/join", RoomRequests.Join("77.4.5.6:41320", proto: 12));

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task Polling_is_idempotent_so_a_lost_answer_cannot_break_the_pairing()
    {
        using var factory = new LobbyFactory();
        var host = factory.CallerFrom("88.1.2.3");
        var created = await (await host.PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080")))
            .Read<CreateResult>();

        await factory.CallerFrom("77.4.5.6")
            .PostJson($"/api/v1/rooms/{created.Id}/join", RoomRequests.Join("77.4.5.6:41320"));

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var poll = await (await host.PostJson($"/api/v1/rooms/{created.Id}/poll",
                new { token = created.Token })).Read<PollResult>();
            Assert.Equal("77.4.5.6:41320", poll.Peer!.Public);
        }
    }

    [Fact]
    public async Task Only_the_host_can_poll_or_delete()
    {
        using var factory = new LobbyFactory();
        var created = await (await factory.CallerFrom("88.1.2.3")
            .PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080"))).Read<CreateResult>();

        var intruder = factory.CallerFrom("77.4.5.6");

        var poll = await intruder.PostJson($"/api/v1/rooms/{created.Id}/poll", new { token = "nope" });
        Assert.Equal(HttpStatusCode.Forbidden, poll.StatusCode);

        var delete = await intruder.DeleteAsync($"/api/v1/rooms/{created.Id}?token=nope");
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    [Fact]
    public async Task Cancelling_takes_the_room_off_the_board_at_once()
    {
        using var factory = new LobbyFactory();
        var host = factory.CallerFrom("88.1.2.3");
        var created = await (await host.PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080")))
            .Read<CreateResult>();

        var deleted = await host.DeleteAsync($"/api/v1/rooms/{created.Id}?token={created.Token}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var list = await (await factory.CallerFrom("77.4.5.6").GetAsync("/api/v1/rooms?proto=11"))
            .Read<ListResult>();
        Assert.Empty(list.Rooms);

        var poll = await host.PostJson($"/api/v1/rooms/{created.Id}/poll", new { token = created.Token });
        Assert.Equal(HttpStatusCode.NotFound, poll.StatusCode);
    }

    [Fact]
    public async Task A_claim_that_goes_nowhere_puts_the_room_back_on_the_board()
    {
        using var factory = new LobbyFactory(("ClaimedTtlSeconds", "1"));
        var host = factory.CallerFrom("88.1.2.3");
        var created = await (await host.PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080")))
            .Read<CreateResult>();

        await factory.CallerFrom("77.4.5.6")
            .PostJson($"/api/v1/rooms/{created.Id}/join", RoomRequests.Join("77.4.5.6:41320"));

        await Task.Delay(1300);

        /* The client walked away: the host is not left advertising a room nobody
         * can join for the rest of its TTL. */
        var list = await (await factory.CallerFrom("12.13.14.15").GetAsync("/api/v1/rooms?proto=11"))
            .Read<ListResult>();
        Assert.Single(list.Rooms);

        var poll = await (await host.PostJson($"/api/v1/rooms/{created.Id}/poll",
            new { token = created.Token })).Read<PollResult>();
        Assert.Equal("open", poll.State);
        Assert.Null(poll.Peer);
    }

    [Fact]
    public async Task A_room_whose_host_stopped_polling_expires()
    {
        using var factory = new LobbyFactory(("OpenTtlSeconds", "1"));
        var created = await (await factory.CallerFrom("88.1.2.3")
            .PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080"))).Read<CreateResult>();

        await Task.Delay(1300);

        var list = await (await factory.CallerFrom("77.4.5.6").GetAsync("/api/v1/rooms?proto=11"))
            .Read<ListResult>();
        Assert.Empty(list.Rooms);

        var join = await factory.CallerFrom("77.4.5.6")
            .PostJson($"/api/v1/rooms/{created.Id}/join", RoomRequests.Join("77.4.5.6:41320"));
        Assert.Equal(HttpStatusCode.NotFound, join.StatusCode);
    }

    [Fact]
    public async Task Host_polling_keeps_its_own_room_alive()
    {
        using var factory = new LobbyFactory(("OpenTtlSeconds", "2"));
        var host = factory.CallerFrom("88.1.2.3");
        var created = await (await host.PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080")))
            .Read<CreateResult>();

        for (var beat = 0; beat < 3; beat++)
        {
            await Task.Delay(700);
            var poll = await host.PostJson($"/api/v1/rooms/{created.Id}/poll", new { token = created.Token });
            Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
        }

        var list = await (await factory.CallerFrom("77.4.5.6").GetAsync("/api/v1/rooms?proto=11"))
            .Read<ListResult>();
        Assert.Single(list.Rooms);
    }

    [Fact]
    public async Task Viewers_browsing_the_board_are_counted_for_the_host_dialog()
    {
        using var factory = new LobbyFactory();
        var host = factory.CallerFrom("88.1.2.3");
        var created = await (await host.PostJson("/api/v1/rooms", RoomRequests.Create("88.1.2.3:2080")))
            .Read<CreateResult>();

        await factory.CallerFrom("77.4.5.6").GetAsync("/api/v1/rooms?proto=11");
        await factory.CallerFrom("12.13.14.15").GetAsync("/api/v1/rooms?proto=11");

        var poll = await (await host.PostJson($"/api/v1/rooms/{created.Id}/poll",
            new { token = created.Token })).Read<PollResult>();

        Assert.Equal(2, poll.Viewers);
    }
}
