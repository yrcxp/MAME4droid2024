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

using Serilog;
using Serilog.Events;
using Serilog.Filters;

namespace Mame4droid.Lobby.Services;

/// Two files, on purpose. Telemetry is the record worth keeping and reading, so
/// it gets one file per month and a year of history; operational noise (startup,
/// warnings, errors) rolls daily and is thrown away after a week. Looking up
/// what happened last week is then one file, not seven.
///
/// There is deliberately nothing per request: on a plan with a 60 min/day CPU
/// budget, request logging is the first thing that would eat it. Application
/// Insights is out of the question for the same reason.
public static class LobbyLogging
{
    private const int OperationalDaysKept = 7;
    private const int TelemetryMonthsKept = 12;

    /* Serilog defaults to a gigabyte per file, and the endpoint that feeds the
     * telemetry log is anonymous: a flood would fill the disk long before the
     * month rolled and take the whole server with it. With a size cap and
     * rollOnFileSizeLimit the retained count becomes a real ceiling. */
    private const long TelemetryFileBytes = 4L * 1024 * 1024;
    private const long OperationalFileBytes = 2L * 1024 * 1024;

    /// One line per session outcome, no level or source noise: these files are
    /// meant to be read and grepped by a person.
    private const string TelemetryLine = "{Timestamp:yyyy-MM-dd HH:mm:ss} {Message:lj}{NewLine}";

    public static void Configure(IHostEnvironment env, LoggerConfiguration cfg)
    {
        var directory = LogDirectory(
            Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME"),
            Environment.GetEnvironmentVariable("HOME"),
            env.ContentRootPath);

        cfg.MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .WriteTo.Console()
            .WriteTo.Logger(telemetry => telemetry
                .Filter.ByIncludingOnly(Matching.FromSource<TelemetrySink>())
                .WriteTo.File(
                    Path.Combine(directory, "telemetry-.log"),
                    outputTemplate: TelemetryLine,
                    rollingInterval: RollingInterval.Month,
                    retainedFileCountLimit: TelemetryMonthsKept,
                    fileSizeLimitBytes: TelemetryFileBytes,
                    rollOnFileSizeLimit: true,
                    shared: true))
            .WriteTo.Logger(operational => operational
                .Filter.ByExcluding(Matching.FromSource<TelemetrySink>())
                .WriteTo.File(
                    Path.Combine(directory, "lobby-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: OperationalDaysKept,
                    fileSizeLimitBytes: OperationalFileBytes,
                    rollOnFileSizeLimit: true,
                    shared: true));
    }

    /// The LogFiles folder under HOME is persistent storage on the host, so
    /// telemetry outlives the very restarts that wipe the rooms. The site name
    /// is what tells us we are there: HOME alone also exists on a dev box,
    /// where honouring it scattered logs through the user profile.
    public static string LogDirectory(string? siteName, string? home, string contentRoot)
        => !string.IsNullOrEmpty(siteName) && !string.IsNullOrEmpty(home) && Directory.Exists(home)
            ? Path.Combine(home, "LogFiles", "lobby")
            : Path.Combine(contentRoot, "logs");
}
