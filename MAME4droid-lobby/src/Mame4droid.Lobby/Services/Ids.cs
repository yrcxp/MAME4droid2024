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

using System.Security.Cryptography;
using System.Text;

namespace Mame4droid.Lobby.Services;

/// Room ids and host tokens. Ids are short enough to appear in a URL and wide
/// enough (32^8) that the listing cannot be walked by guessing.
public static class Ids
{
    /* Crockford-style alphabet: no I, L, O or U, so an id read aloud or typed
     * into the advanced-server pref cannot be mistaken for another one. */
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string NewRoomId()
    {
        var bytes = RandomNumberGenerator.GetBytes(8);
        return string.Create(8, bytes, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = Alphabet[source[i] & 31];
        });
    }

    public static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TokenEquals(string expected, string supplied)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(supplied));

    /// A room's PIN is never kept, only this. The room's own token is the key,
    /// so the hash is salted per room for free and dies with it -- a PIN worth
    /// four digits must not be sitting in memory in the clear, and two rooms
    /// sharing a PIN must not be visibly sharing anything.
    public static string HashPin(string roomToken, string pin)
        => Convert.ToHexString(new HMACSHA256(Encoding.UTF8.GetBytes(roomToken))
            .ComputeHash(Encoding.UTF8.GetBytes(pin)));

    public static bool PinEquals(string expectedHash, string candidateHash)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expectedHash), Encoding.ASCII.GetBytes(candidateHash));
}
