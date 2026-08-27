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

using System.Reflection;

namespace Mame4droid.Lobby.Services;

/// Which build is actually running, printed on the home page and on the first
/// line of the log. The stamp is baked into the assembly at compile time, so a
/// stale package deployed by mistake shows the OLD value -- which is the whole
/// point: it answers "is what I just uploaded the thing serving requests".
public static class BuildInfo
{
    public static string Version { get; } = ReadVersion();

    /// When the package was produced, from the build.txt the publish step
    /// leaves beside the binaries. The same file sits in the local publish
    /// folder, so the two can be compared without guessing.
    public static string Published { get; } = ReadPublished();

    /// When this process came up. The site is put to sleep after a spell with
    /// no traffic and the next request wakes it, so this doubles as "when did
    /// it last wake up": there is no going-to-sleep event to record, the
    /// process simply ends.
    public static DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;

    /// Time since Started, off a monotonic clock so a time correction cannot
    /// make it jump or run backwards.
    public static TimeSpan Uptime => s_uptime.Elapsed;

    /// Uptime at the coarsest unit that still says something useful.
    public static string UptimeText
    {
        get
        {
            var t = Uptime;
            if (t.TotalMinutes < 1) return $"{t.Seconds}s";
            if (t.TotalHours < 1) return $"{t.Minutes}m";
            if (t.TotalDays < 1) return $"{t.Hours}h {t.Minutes}m";
            return $"{(int)t.TotalDays}d {t.Hours}h";
        }
    }

    private static readonly System.Diagnostics.Stopwatch s_uptime = System.Diagnostics.Stopwatch.StartNew();

    private static string ReadVersion()
    {
        var stamped = typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return string.IsNullOrWhiteSpace(stamped) ? "dev" : stamped;
    }

    private static string ReadPublished()
    {
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "build.txt");
            foreach (var line in File.Exists(file) ? File.ReadAllLines(file) : [])
                if (line.StartsWith("published=", StringComparison.Ordinal))
                    return line["published=".Length..].Trim();
        }
        catch
        {
            /* Running from a plain build, or the file is unreadable: the
             * assembly stamp above still names the build. */
        }
        return "unknown";
    }
}
