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
using System.Net.Sockets;

namespace Mame4droid.Lobby.Services;

/// Parsing and sanity checks for the "ip:port" strings the peers exchange.
/// Nothing is ever rewritten: a NAT can hand out a port unrelated to the one
/// configured, so the tuple is stored exactly as STUN reported it.
public static class NetTuple
{
    public const int MaxLength = 64;

    public static bool TryParse(string? text, out IPAddress ip, out int port)
    {
        ip = IPAddress.None;
        port = 0;
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength) return false;

        string host;
        var portText = string.Empty;

        if (text[0] == '[')
        {
            var close = text.IndexOf(']');
            if (close < 2) return false;
            host = text.Substring(1, close - 1);
            if (close + 1 < text.Length)
            {
                if (text[close + 1] != ':') return false;
                portText = text[(close + 2)..];
            }
        }
        else
        {
            var first = text.IndexOf(':');
            if (first > 0 && text.IndexOf(':', first + 1) < 0)
            {
                host = text[..first];
                portText = text[(first + 1)..];
            }
            else
            {
                host = text;
            }
        }

        if (!IPAddress.TryParse(host, out var parsed)) return false;
        if (parsed.IsIPv4MappedToIPv6) parsed = parsed.MapToIPv4();

        if (portText.Length > 0)
        {
            if (!int.TryParse(portText, out port) || port <= 0 || port > 65535) return false;
        }

        ip = parsed;
        return true;
    }

    /// A tuple with an explicit, non-zero port -- what a rendezvous needs.
    public static bool TryParseWithPort(string? text, out IPAddress ip, out int port)
        => TryParse(text, out ip, out port) && port > 0;

    /// Rejects anything that cannot be a public STUN result. 100.64/10 is left
    /// in: a CGNAT subscriber can legitimately see it, and the reflection check
    /// deals with it anyway.
    public static bool IsRoutable(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            return false;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return !ip.IsIPv6LinkLocal && !ip.IsIPv6SiteLocal && !ip.IsIPv6Multicast
                   && !IsUniqueLocalV6(ip);

        var b = ip.GetAddressBytes();
        if (b[0] == 10) return false;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
        if (b[0] == 192 && b[1] == 168) return false;
        if (b[0] == 169 && b[1] == 254) return false;
        if (b[0] == 127 || b[0] == 0 || b[0] >= 224) return false;
        return true;
    }

    public static bool IsPrivateV4(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168);
    }

    private static bool IsUniqueLocalV6(IPAddress ip) => (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
}
