using System.Collections.Concurrent;
using System.Net;
using Mame4droid.Lobby.Configuration;
using Mame4droid.Lobby.Model;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Mame4droid.Lobby.Services;

public enum StoreResult
{
    Ok,
    NotFound,
    AlreadyClaimed,
    ProtoMismatch,
    Forbidden,
    QuotaExceeded,
    BadPin,
    PinBlocked
}

/// The whole state of the service. IMemoryCache holds the entries (one small
/// instance, so it is correct by construction) and a parallel dictionary makes
/// them enumerable, which MemoryCache does not expose. Expiry is applied lazily
/// on access: a free server gives no background timer worth relying on.
public sealed class RoomStore
{
    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<LobbyOptions> _options;
    private readonly ConcurrentDictionary<string, Room> _index = new(StringComparer.Ordinal);
    private long _version;

    public RoomStore(IMemoryCache cache, IOptionsMonitor<LobbyOptions> options)
    {
        _cache = cache;
        _options = options;
    }

    /// Bumped by every change that alters the listing; drives the ETag.
    public long Version => Interlocked.Read(ref _version);

    public StoreResult TryCreate(Room room)
    {
        var o = _options.CurrentValue;
        var now = DateTimeOffset.UtcNow;

        Sweep(now);
        if (CountForOwner(room.OwnerKey) >= o.MaxRoomsPerIp) return StoreResult.QuotaExceeded;

        room.CreatedUtc = now;
        room.ExpiresUtc = now.AddSeconds(o.OpenTtlSeconds);
        room.State = RoomState.Open;

        _index[room.Id] = room;
        Track(room);
        Bump();
        return StoreResult.Ok;
    }

    public Room? Get(string id)
    {
        if (!_index.TryGetValue(id, out var room)) return null;
        return Settle(room, DateTimeOffset.UtcNow) ? null : room;
    }

    /// Claim transition. The state test and the write happen under the room's
    /// own lock, so of two clients racing for the same room exactly one gets Ok
    /// and the other a clean AlreadyClaimed.
    public StoreResult TryJoin(string id, int proto, PeerEntry peer, string? pin, out Room? room)
    {
        room = Get(id);
        if (room is null) return StoreResult.NotFound;
        if (room.Proto != proto) return StoreResult.ProtoMismatch;

        var o = _options.CurrentValue;
        var now = DateTimeOffset.UtcNow;

        lock (room.Sync)
        {
            if (room.IsExpired(now)) return StoreResult.NotFound;

            if (room.IsLocked)
            {
                /* Guessing is what a four-digit secret invites, so the room
                 * stops answering after a few misses rather than relying on a
                 * per-caller rate limit an attacker can spread out. */
                if (room.PinFailures >= o.MaxPinAttempts) return StoreResult.PinBlocked;

                if (string.IsNullOrEmpty(pin)
                    || !Ids.PinEquals(room.PinHash!, Ids.HashPin(room.Token, pin)))
                {
                    room.PinFailures++;
                    return StoreResult.BadPin;
                }
            }

            if (room.State != RoomState.Open) return StoreResult.AlreadyClaimed;

            room.State = RoomState.Claimed;
            room.Peer = peer;
            room.ClaimedUntil = now.AddSeconds(o.ClaimedTtlSeconds);

            /* Keep the room alive at least as long as the claim: the host must
             * still have time to poll for the peer it was just handed. */
            var floor = now.AddSeconds(o.ClaimedTtlSeconds);
            if (room.ExpiresUtc < floor) room.ExpiresUtc = floor;
        }

        Track(room);
        Bump();
        return StoreResult.Ok;
    }

    /// Replaces the claimant's tuple with the one it learned after its own STUN
    /// pass. Only the address that claimed the room may do this, and only while
    /// the claim stands.
    public StoreResult TryUpdatePeer(string id, int proto, IPAddress caller, PeerEntry peer)
    {
        var room = Get(id);
        if (room is null) return StoreResult.NotFound;
        if (room.Proto != proto) return StoreResult.ProtoMismatch;

        lock (room.Sync)
        {
            if (room.State != RoomState.Claimed || room.Peer is null) return StoreResult.NotFound;
            if (!ClientAddress.SameSite(room.Peer.ObservedIp, caller)) return StoreResult.Forbidden;

            room.Peer = peer;
        }
        return StoreResult.Ok;
    }

    /// Heartbeat. Never consumes the peer: the same answer is returned until the
    /// host deletes the room, so one lost response cannot break the pairing.
    public StoreResult TryPoll(string id, string? token, out Room? room)
    {
        room = Get(id);
        if (room is null) return StoreResult.NotFound;
        if (!TokenMatches(room, token)) return StoreResult.Forbidden;

        var now = DateTimeOffset.UtcNow;
        lock (room.Sync)
        {
            var extended = now.AddSeconds(_options.CurrentValue.OpenTtlSeconds);
            if (room.ExpiresUtc < extended) room.ExpiresUtc = extended;
        }

        Track(room);
        return StoreResult.Ok;
    }

    public StoreResult TryDelete(string id, string? token)
    {
        var room = Get(id);
        if (room is null) return StoreResult.NotFound;
        if (!TokenMatches(room, token)) return StoreResult.Forbidden;

        Remove(room);
        return StoreResult.Ok;
    }

    /// Open rooms of one protocol, newest first. Callers get a plain snapshot;
    /// no lock is held while a response is serialised.
    public IReadOnlyList<Room> ListOpen(int proto, int max, out int total)
    {
        Sweep(DateTimeOffset.UtcNow);

        var open = _index.Values
            .Where(r => r.Proto == proto && r.State == RoomState.Open)
            .OrderByDescending(r => r.CreatedUtc)
            .ToList();

        total = open.Count;
        return open.Count > max ? open.GetRange(0, max) : open;
    }

    public int CountForOwner(string ownerKey)
        => _index.Values.Count(r => string.Equals(r.OwnerKey, ownerKey, StringComparison.Ordinal));

    private static bool TokenMatches(Room room, string? token)
        => !string.IsNullOrEmpty(token) && Ids.TokenEquals(room.Token, token);

    /// Applies every time-based transition in one place. Returns true when the
    /// room is gone.
    private bool Settle(Room room, DateTimeOffset now)
    {
        if (room.IsExpired(now))
        {
            Remove(room);
            return true;
        }

        var reverted = false;
        lock (room.Sync)
        {
            /* A claim that never turned into a session goes back on the board
             * instead of holding the host hostage for the rest of its TTL. */
            if (room.State == RoomState.Claimed && now >= room.ClaimedUntil)
            {
                room.State = RoomState.Open;
                room.Peer = null;
                reverted = true;
            }
        }

        if (reverted) Bump();
        return false;
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var room in _index.Values) Settle(room, now);
    }

    private void Remove(Room room)
    {
        if (_index.TryRemove(new KeyValuePair<string, Room>(room.Id, room)))
        {
            _cache.Remove(room.Id);
            Bump();
        }
    }

    /// (Re)arms the cache entry. The absolute expiration has to be pushed out on
    /// every heartbeat, since an existing entry's expiry cannot be edited.
    private void Track(Room room)
    {
        var ttl = room.ExpiresUtc - DateTimeOffset.UtcNow;
        if (ttl <= TimeSpan.Zero) ttl = TimeSpan.FromSeconds(1);

        var entry = new MemoryCacheEntryOptions
        {
            /* Safety net only: the authoritative deadline is room.ExpiresUtc.
             * The margin keeps the cache from dropping a room the lazy sweep
             * still considers alive. */
            AbsoluteExpirationRelativeToNow = ttl + TimeSpan.FromSeconds(30)
        };

        entry.RegisterPostEvictionCallback(static (key, value, reason, state) =>
        {
            if (reason == EvictionReason.Replaced) return;
            if (value is Room evicted && state is RoomStore store)
                store._index.TryRemove(new KeyValuePair<string, Room>((string)key, evicted));
        }, this);

        _cache.Set(room.Id, room, entry);
    }

    private void Bump() => Interlocked.Increment(ref _version);
}
