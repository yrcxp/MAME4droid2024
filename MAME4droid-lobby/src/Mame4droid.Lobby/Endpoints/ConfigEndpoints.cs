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

using Mame4droid.Lobby.Configuration;
using Mame4droid.Lobby.Contracts;
using Mame4droid.Lobby.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Mame4droid.Lobby.Endpoints;

public static class ConfigEndpoints
{
    /// First call the client makes. It carries the kill switch and every poll
    /// cadence, so the load this service takes can be changed from the server
    /// without anyone updating an APK.
    public static void MapConfigEndpoints(this WebApplication routes)
    {
        routes.MapGet("/api/v1/config", (
                IOptionsMonitor<LobbyOptions> options,
                StatsStore stats,
                [FromQuery(Name = "proto")] int? proto,
                [FromQuery(Name = "app")] string? appVersion) =>
            {
                var o = options.CurrentValue;
                var recent = stats.Snapshot();
                return Results.Json(new ConfigResponse(
                    o.Enabled,
                    o.PollSeconds,
                    o.ListSeconds,
                    o.ListBackoff,
                    o.ListMaxSeconds,
                    o.MinApp,
                    o.Notice,
                    /* Advice only. A build older than minApp is still served:
                     * refusing it would turn a soft nudge into an outage. */
                    AppVersion.IsOlder(appVersion, o.MinApp),
                    /* Omitted entirely while nothing has cleared its threshold,
                     * so an old build sees the field it always saw and a new
                     * one has nothing to draw rather than a row of zeros. */
                    recent.Interesting
                        ? new StatsDto(recent.Since, recent.Rooms, recent.Played,
                                       recent.Games.ToArray(), recent.Best.ToArray(),
                                       recent.Countries, recent.Flags.ToArray())
                        : null));
            })
            .RequireRateLimiting(RateLimitPolicies.Config)
            .NoStore();

        /* Cheap liveness probe that does not touch the store, useful to warm the
         * instance up after an idle unload. */
        routes.MapGet("/api/v1/health", () => Results.Text("ok"))
            .RequireRateLimiting(RateLimitPolicies.Health)
            .NoStore();
    }
}
