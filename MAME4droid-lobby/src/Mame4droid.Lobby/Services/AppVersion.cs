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

namespace Mame4droid.Lobby.Services;

/// Numeric-only version compare ("1.38.3" style), enough for versionName. The
/// server never refuses a request over this: minApp is advice returned by
/// /config so an update can be nudged without breaking anyone mid-session.
public static class AppVersion
{
    public static int Compare(string? left, string? right)
    {
        var a = Parse(left);
        var b = Parse(right);

        for (var i = 0; i < 4; i++)
        {
            if (a[i] != b[i]) return a[i] < b[i] ? -1 : 1;
        }
        return 0;
    }

    public static bool IsOlder(string? app, string? minimum)
        => !string.IsNullOrEmpty(minimum) && !string.IsNullOrEmpty(app) && Compare(app, minimum) < 0;

    private static int[] Parse(string? version)
    {
        var parts = new int[4];
        if (string.IsNullOrEmpty(version)) return parts;

        var fields = version.Split('.');
        for (var i = 0; i < parts.Length && i < fields.Length; i++)
            parts[i] = int.TryParse(fields[i], out var value) && value >= 0 ? value : 0;

        return parts;
    }
}
