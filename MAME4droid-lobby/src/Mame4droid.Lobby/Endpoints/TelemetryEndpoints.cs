using Mame4droid.Lobby.Contracts;
using Mame4droid.Lobby.Services;

namespace Mame4droid.Lobby.Endpoints;

public static class TelemetryEndpoints
{
    /// Fire and forget from the client's point of view: it reports how the
    /// session ended and never waits for or acts on the answer.
    public static void MapTelemetryEndpoints(this WebApplication routes)
    {
        routes.MapPost("/api/v1/telemetry", (TelemetryRequest report, TelemetrySink sink) =>
            {
                sink.Record(report);

                /* Even a malformed report answers 204: this endpoint must never
                 * give a client a reason to retry. */
                return Results.NoContent();
            })
            .RequireRateLimiting(RateLimitPolicies.Telemetry);
    }
}
