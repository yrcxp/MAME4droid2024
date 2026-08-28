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
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Mame4droid.Lobby.Services;

/// Lets a client tell "this room is behind my own router" without anybody
/// learning an address. Each room carries the salted hash of its host's
/// address and the caller gets its own in a response header, so the two are
/// compared on the device.
///
/// The hash is in the shared body and the caller's own is a header, on purpose:
/// a per-caller body would give every viewer a different ETag and undo the 304s
/// the traffic budget depends on. The salt is new on every start, so nothing
/// here survives a restart or can be matched against anything else.
public sealed class SiteHash
{
    public const string HeaderName = "X-Lobby-Site";

    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(16);
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    public string Of(IPAddress? ip)
    {
        if (ip is null) return "";

        /* IPv6 is keyed per /64: one subscriber gets a whole prefix, and two
         * devices in a house rarely share the exact address. */
        /* Memoisation only: without a lid a horde of distinct callers could
         * grow this for as long as the instance stays up. Recomputing after
         * a clear costs one HMAC, so the lid can be generous. */
        if (_cache.Count >= 65536) _cache.Clear();

        var key = ClientAddress.PartitionKey(ip);

        return _cache.GetOrAdd(key, static (k, salt) =>
            Convert.ToHexString(HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(k)), 0, 4),
            _salt);
    }
}
