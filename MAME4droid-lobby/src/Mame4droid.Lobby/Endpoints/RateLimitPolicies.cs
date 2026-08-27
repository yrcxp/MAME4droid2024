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

using System.Threading.RateLimiting;
using Mame4droid.Lobby.Configuration;
using Mame4droid.Lobby.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Mame4droid.Lobby.Endpoints;

/// One bucket per endpoint per caller. A single global budget would starve the
/// host's own poll, which is this protocol's heartbeat: run out and the room
/// evaporates mid-negotiation. "Caller" is a public address, not a device:
/// everyone behind one router counts as one, which is why the numbers in
/// RateLimitOptions are sized for a roomful of players.
public static class RateLimitPolicies
{
    public const string Config = "lobby-config";
    public const string Health = "lobby-health";
    public const string List = "lobby-list";
    public const string Create = "lobby-create";
    public const string Join = "lobby-join";
    public const string Poll = "lobby-poll";
    public const string Telemetry = "lobby-telemetry";

    public static void AddLobbyRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            /* Tell the client how long to back off instead of letting it hammer
             * away; the Android side honours Retry-After. */
            limiter.OnRejected = (ctx, _) =>
            {
                ctx.HttpContext.Response.Headers.RetryAfter = "30";
                return ValueTask.CompletedTask;
            };

            AddPolicy(limiter, Config, r => r.Config);
            AddPolicy(limiter, Health, r => r.Health);
            AddPolicy(limiter, List, r => r.List);
            AddPolicy(limiter, Create, r => r.Create);
            AddPolicy(limiter, Join, r => r.Join);
            AddPolicy(limiter, Poll, r => r.Poll);
            AddPolicy(limiter, Telemetry, r => r.Telemetry);
        });
    }

    private static void AddPolicy(RateLimiterOptions limiter, string name, Func<RateLimitOptions, int> limit)
    {
        limiter.AddPolicy(name, ctx =>
        {
            /* Read per request, not once at startup, so a limit can be tightened
             * from appsettings.json without a redeploy. */
            var options = ctx.RequestServices
                .GetRequiredService<IOptionsMonitor<LobbyOptions>>().CurrentValue;

            var ip = ClientAddress.Resolve(ctx);
            var permits = Math.Max(1, limit(options.RateLimits));

            return RateLimitPartition.GetSlidingWindowLimiter(
                $"{name}|{permits}|{ClientAddress.PartitionKey(ip)}",
                _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = permits,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6,
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
        });
    }
}
