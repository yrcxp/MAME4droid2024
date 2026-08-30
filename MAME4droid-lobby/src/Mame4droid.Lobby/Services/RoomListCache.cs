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
using System.Text.Json;
using Mame4droid.Lobby.Configuration;
using Mame4droid.Lobby.Contracts;
using Mame4droid.Lobby.Model;
using Mame4droid.Lobby.Serialization;
using Microsoft.Extensions.Options;

namespace Mame4droid.Lobby.Services;

/// The listing is serialised once per change and handed to everyone, and its
/// ETag only moves when the room set does -- which turns a screen left open
/// into a stream of ~200 byte 304s instead of a full body every few seconds.
/// On a free server that is the difference between fitting the traffic
/// allowance and not. The ETag is per protocol: so is the response.
public sealed class RoomListCache
{
    private readonly RoomStore _store;
    private readonly IOptionsMonitor<LobbyOptions> _options;
    private readonly SiteHash _sites;
    private readonly ConcurrentDictionary<int, Snapshot> _byProto = new();

    public RoomListCache(RoomStore store, IOptionsMonitor<LobbyOptions> options, SiteHash sites)
    {
        _store = store;
        _options = options;
        _sites = sites;
    }

    public Snapshot Get(int proto)
    {
        /* ListOpen sweeps first, so an expiry has already bumped the version by
         * the time it is read here. */
        var rooms = _store.ListOpen(proto, _options.CurrentValue.MaxRoomsListed, out var total);
        var version = _store.Version;

        if (_byProto.TryGetValue(proto, out var cached) && cached.Version == version)
            return cached;

        var payload = new RoomListResponse(rooms.Select(ToSummary).ToList(), total);
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, LobbyJsonContext.Default.RoomListResponse);
        var snapshot = new Snapshot(version, body, $"W/\"{proto}-{version}\"");

        _byProto[proto] = snapshot;
        return snapshot;
    }

    private RoomSummary ToSummary(Room room) => new(
        room.Id,
        room.Game,
        room.Host.Country,
        room.Mode,
        room.Delay,
        room.Plugins,
        new NatDto(room.Host.Nat.Sym, room.Host.Nat.Pp, room.Host.Nat.Upnp, room.Host.Nat.V6, room.Host.Nat.Mob),
        room.Host.Verified,
        room.Host.Lan.Length > 0,
        room.App,
        /* Absolute, not "waiting for N seconds": a value that ticks every second
         * would change the body constantly and kill the 304s above. */
        room.CreatedUtc.ToUnixTimeSeconds(),
        _sites.Of(room.Host.ObservedIp),
        room.IsLocked,
        room.Playing);

    public sealed record Snapshot(long Version, byte[] Body, string ETag);
}
