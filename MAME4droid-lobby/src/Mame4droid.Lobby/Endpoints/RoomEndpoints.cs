using Mame4droid.Lobby.Configuration;
using Mame4droid.Lobby.Contracts;
using Mame4droid.Lobby.Model;
using Mame4droid.Lobby.Services;
using Microsoft.Extensions.Options;

namespace Mame4droid.Lobby.Endpoints;

public static class RoomEndpoints
{
    public static void MapRoomEndpoints(this WebApplication app)
    {
        var rooms = app.MapGroup("/api/v1/rooms");

        rooms.MapPost("", CreateRoom).RequireRateLimiting(RateLimitPolicies.Create);
        rooms.MapGet("", ListRooms).RequireRateLimiting(RateLimitPolicies.List);
        rooms.MapPost("/{id}/join", JoinRoom).RequireRateLimiting(RateLimitPolicies.Join);
        rooms.MapPost("/{id}/peer", UpdatePeer).RequireRateLimiting(RateLimitPolicies.Join);
        rooms.MapPost("/{id}/poll", PollRoom).RequireRateLimiting(RateLimitPolicies.Poll);
        rooms.MapDelete("/{id}", DeleteRoom).RequireRateLimiting(RateLimitPolicies.Poll);
    }

    private static IResult CreateRoom(
        HttpContext ctx,
        CreateRoomRequest request,
        RoomStore store,
        IOptionsMonitor<LobbyOptions> options)
    {
        var o = options.CurrentValue;

        if (!RequestValidation.IsValidProto(request.Proto)) return Fail("bad_proto");
        if (!RequestValidation.IsValidGame(request.Game)) return Fail("bad_game");
        if (request.Mode is < 0 or > 1) return Fail("bad_mode");
        if (request.Delay is < 0 or > 20) return Fail("bad_delay");

        /* A malformed PIN is refused rather than quietly dropped: publishing a
         * room the host believes is private would be worse than an error. */
        var wantsPin = !string.IsNullOrEmpty(request.Pin);
        if (wantsPin && !RequestValidation.IsValidPin(request.Pin)) return Fail("bad_pin");

        var observed = ClientAddress.Resolve(ctx);
        var validation = RequestValidation.ValidatePeer(
            request.Public, request.PublicAlt, request.Lan, request.Nat, request.Country,
            observed, o, requirePublic: true);

        if (!validation.Ok) return Fail(validation.Error!);

        var token = Ids.NewToken();
        var room = new Room
        {
            Id = Ids.NewRoomId(),
            Token = token,
            OwnerKey = ClientAddress.PartitionKey(observed),
            Proto = request.Proto,
            App = RequestValidation.NormaliseApp(request.App),
            Game = request.Game!,
            Mode = request.Mode,
            Delay = request.Delay,
            Plugins = request.Plugins,
            Host = validation.Peer!,
            Playing = request.Playing,
            PinHash = wantsPin ? Ids.HashPin(token, request.Pin!) : null
        };

        if (store.TryCreate(room) == StoreResult.QuotaExceeded)
        {
            /* Same shape as a rate-limit refusal, so the client has one rule for
             * backing off instead of two. */
            ctx.Response.Headers.RetryAfter = o.OpenTtlSeconds.ToString();
            return Results.Json(new ErrorResponse("too_many_rooms"), statusCode: StatusCodes.Status429TooManyRequests);
        }

        return Results.Json(new CreateRoomResponse(
            room.Id, room.Token, o.OpenTtlSeconds, o.PollSeconds, room.Host.Verified));
    }

    /// Served from a pre-serialised snapshot with an ETag, so a board left open
    /// costs a 304 with no body until something actually changes.
    private static async Task ListRooms(
        HttpContext ctx,
        RoomListCache cache,
        ViewerCounter viewers,
        SiteHash sites,
        IOptionsMonitor<LobbyOptions> options,
        int? proto)
    {
        if (proto is null || !RequestValidation.IsValidProto(proto.Value))
        {
            await WriteError(ctx, StatusCodes.Status400BadRequest, "bad_proto");
            return;
        }

        var observed = ClientAddress.Resolve(ctx);
        viewers.Touch(observed);

        var snapshot = cache.Get(proto.Value);
        ctx.Response.Headers.ETag = snapshot.ETag;
        ctx.Response.Headers.CacheControl = "no-cache";

        /* The caller's own site, so it can spot the rooms behind its own
         * router. A header, not a field: it keeps the body the same for
         * everyone, and it still arrives with a 304. */
        ctx.Response.Headers[SiteHash.HeaderName] = sites.Of(observed);

        /* Deliberately no "waiting for N seconds" in the body: the client works
         * it out from the absolute "since" and the standard Date header. */
        var known = ctx.Request.Headers.IfNoneMatch.ToString();
        if (known.Length > 0 && known.Contains(snapshot.ETag, StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength = snapshot.Body.Length;
        await ctx.Response.Body.WriteAsync(snapshot.Body);
    }

    /// Hands the host's addresses to the client and, just as importantly, keeps
    /// the client's own tuple so the host can aim its punch back: hole punching
    /// only works if both ends learn about each other.
    private static IResult JoinRoom(
        HttpContext ctx,
        string id,
        JoinRequest request,
        RoomStore store,
        IOptionsMonitor<LobbyOptions> options)
    {
        var o = options.CurrentValue;
        if (!RequestValidation.IsValidProto(request.Proto)) return Fail("bad_proto");

        var observed = ClientAddress.Resolve(ctx);

        /* The client may not have run STUN yet, so its tuple is optional here;
         * the observed address alone still lets the host tell LAN from WAN. */
        var validation = RequestValidation.ValidatePeer(
            request.Public, request.PublicAlt, request.Lan, request.Nat, request.Country,
            observed, o, requirePublic: false);

        if (!validation.Ok) return Fail(validation.Error!);

        var result = store.TryJoin(id, request.Proto, validation.Peer!, request.Pin, out var room);
        switch (result)
        {
            case StoreResult.BadPin:
                return Results.Json(new ErrorResponse("bad_pin"),
                    statusCode: StatusCodes.Status403Forbidden);
            case StoreResult.PinBlocked:
                /* Too many guesses: the room is done answering. */
                return Results.Json(new ErrorResponse("pin_blocked"),
                    statusCode: StatusCodes.Status423Locked);
            case StoreResult.NotFound:
                return Results.Json(new ErrorResponse("not_found"), statusCode: StatusCodes.Status404NotFound);
            case StoreResult.AlreadyClaimed:
                return Results.Json(new ErrorResponse("already_claimed"), statusCode: StatusCodes.Status409Conflict);
            case StoreResult.ProtoMismatch:
                return Results.Json(new ErrorResponse("proto_mismatch"), statusCode: StatusCodes.Status412PreconditionFailed);
        }

        var host = room!.Host;
        var sameSite = ClientAddress.SameSite(host.ObservedIp, observed);

        return Results.Json(new JoinResponse(new HostDto(
            host.Public, host.PublicAlt, host.Lan, room.Game, room.Mode, room.Delay, room.Plugins,
            new NatDto(host.Nat.Sym, host.Nat.Pp, host.Nat.Upnp, host.Nat.V6),
            host.Country, host.Verified, sameSite)));
    }

    /// <summary>
    /// Corrects the tuple a client left when it joined.
    ///
    /// A client cannot know its real address until STUN has run on the game
    /// socket, which only happens once the join is under way, so what it sends
    /// with the claim is a guess: right when the NAT preserves the port, and
    /// plain wrong over IPv6, where the pre-join probe is v4-only. Without this
    /// the host aims its punch at an address that never existed, which on two
    /// mobile connections is the difference between playing and not.
    ///
    /// No token exists on this side, so the caller is authenticated the only
    /// honest way available: it must still be the address that claimed the
    /// room. The host picks the correction up on its next poll and re-arms the
    /// punch, which is hot-settable for exactly this reason.
    /// </summary>
    private static IResult UpdatePeer(
        HttpContext ctx,
        string id,
        JoinRequest request,
        RoomStore store,
        IOptionsMonitor<LobbyOptions> options)
    {
        var o = options.CurrentValue;
        if (!RequestValidation.IsValidProto(request.Proto)) return Fail("bad_proto");

        var observed = ClientAddress.Resolve(ctx);
        var validation = RequestValidation.ValidatePeer(
            request.Public, request.PublicAlt, request.Lan, request.Nat, request.Country,
            observed, o, requirePublic: false);

        if (!validation.Ok) return Fail(validation.Error!);

        var result = store.TryUpdatePeer(id, request.Proto, observed, validation.Peer!);
        return result switch
        {
            StoreResult.NotFound => Results.Json(new ErrorResponse("not_found"),
                statusCode: StatusCodes.Status404NotFound),
            StoreResult.ProtoMismatch => Results.Json(new ErrorResponse("proto_mismatch"),
                statusCode: StatusCodes.Status412PreconditionFailed),
            StoreResult.Forbidden => Results.Json(new ErrorResponse("not_your_claim"),
                statusCode: StatusCodes.Status403Forbidden),
            _ => Results.NoContent()
        };
    }

    private static IResult PollRoom(
        string id,
        PollRequest request,
        RoomStore store,
        ViewerCounter viewers,
        IOptionsMonitor<LobbyOptions> options)
    {
        var result = store.TryPoll(id, request.Token, out var room);
        if (result == StoreResult.NotFound)
            return Results.Json(new ErrorResponse("not_found"), statusCode: StatusCodes.Status404NotFound);
        if (result == StoreResult.Forbidden)
            return Results.Json(new ErrorResponse("bad_token"), statusCode: StatusCodes.Status403Forbidden);

        var peer = room!.Peer;
        var dto = peer is null
            ? null
            : new PeerDto(
                peer.Public, peer.PublicAlt, peer.Lan,
                new NatDto(peer.Nat.Sym, peer.Nat.Pp, peer.Nat.Upnp, peer.Nat.V6),
                peer.Country, peer.Verified,
                ClientAddress.SameSite(peer.ObservedIp, room.Host.ObservedIp));

        var ttl = (int)Math.Max(0, (room.ExpiresUtc - DateTimeOffset.UtcNow).TotalSeconds);
        var state = room.State == RoomState.Claimed ? "claimed" : "open";

        return Results.Json(new PollResponse(state, ttl, viewers.Count(), dto));
    }

    private static IResult DeleteRoom(string id, string? token, RoomStore store)
    {
        var result = store.TryDelete(id, token);
        if (result == StoreResult.Forbidden)
            return Results.Json(new ErrorResponse("bad_token"), statusCode: StatusCodes.Status403Forbidden);

        /* An already expired room is the normal case when the host cancels late;
         * report it as done rather than as an error. */
        return Results.NoContent();
    }

    private static IResult Fail(string error)
        => Results.Json(new ErrorResponse(error), statusCode: StatusCodes.Status400BadRequest);

    private static async Task WriteError(HttpContext ctx, int status, string error)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync($"{{\"error\":\"{error}\"}}");
    }
}
