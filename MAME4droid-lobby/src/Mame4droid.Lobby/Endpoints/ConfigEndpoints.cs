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
                [FromQuery(Name = "proto")] int? proto,
                [FromQuery(Name = "app")] string? appVersion) =>
            {
                var o = options.CurrentValue;
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
                    AppVersion.IsOlder(appVersion, o.MinApp)));
            })
            .RequireRateLimiting(RateLimitPolicies.Config);

        /* Cheap liveness probe that does not touch the store, useful to warm the
         * instance up after an idle unload. */
        routes.MapGet("/api/v1/health", () => Results.Text("ok"))
            .RequireRateLimiting(RateLimitPolicies.Health);
    }
}
