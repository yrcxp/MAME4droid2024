using System.Text;
using Mame4droid.Lobby.Configuration;
using Microsoft.Extensions.Options;

namespace Mame4droid.Lobby.Endpoints;

public static class DiagnosticsEndpoints
{
    /// Shows a caller what the platform in front of us says about that same
    /// caller. Which forwarding headers survive, and whether a client can
    /// overwrite them, differs per host and decides whether the whole
    /// anti-reflection check means anything -- so it has to be measured on the
    /// real deployment, not assumed. Off unless Lobby:Diagnostics is set, and
    /// it only ever reflects the requester's own connection.
    public static void MapDiagnosticsEndpoints(this WebApplication routes)
    {
        routes.MapGet("/api/v1/whoami", (HttpContext ctx, IOptionsMonitor<LobbyOptions> options) =>
        {
            if (!options.CurrentValue.Diagnostics) return Results.NotFound();

            var report = new StringBuilder();
            report.Append("remoteIp=").Append(ctx.Connection.RemoteIpAddress?.ToString() ?? "(null)")
                  .Append(" remotePort=").Append(ctx.Connection.RemotePort).Append('\n');

            foreach (var header in ctx.Request.Headers.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!header.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase)
                    && !header.Key.Equals("Forwarded", StringComparison.OrdinalIgnoreCase))
                    continue;

                /* Each value on its own line: a header sent twice arrives as two
                 * entries, and that ordering is exactly what is in question. */
                var values = header.Value;
                for (var i = 0; i < values.Count; i++)
                    report.Append(header.Key).Append('[').Append(i).Append("]=")
                          .Append(values[i]).Append('\n');
            }

            return Results.Text(report.ToString());
        }).RequireRateLimiting(RateLimitPolicies.Config);
    }
}
