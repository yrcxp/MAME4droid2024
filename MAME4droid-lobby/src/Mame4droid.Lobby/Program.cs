using Mame4droid.Lobby.Configuration;
using Mame4droid.Lobby.Contracts;
using Mame4droid.Lobby.Endpoints;
using Mame4droid.Lobby.Serialization;
using Mame4droid.Lobby.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => LobbyLogging.Configure(ctx.HostingEnvironment, cfg));

builder.Services.Configure<LobbyOptions>(builder.Configuration.GetSection(LobbyOptions.SectionName));

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<RoomStore>();
builder.Services.AddSingleton<RoomListCache>();
builder.Services.AddSingleton<ViewerCounter>();
builder.Services.AddSingleton<SiteHash>();
builder.Services.AddSingleton<TelemetrySink>();
builder.Services.AddLobbyRateLimiting();

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, LobbyJsonContext.Default));

var app = builder.Build();

/* Self-hosting behind your own reverse proxy: let the framework rewrite the
 * caller from X-Forwarded-For, the only vetted way to do it. Off by default,
 * and it has to stay off where the host already did it -- doing it twice
 * trusts whatever leftovers the caller put in the header. */
if (app.Services.GetRequiredService<IOptionsMonitor<LobbyOptions>>().CurrentValue.TrustForwardedHeaders)
{
    var forwarding = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor,
        ForwardLimit = 1
    };

    /* Cleared so the proxy in front is trusted whatever its address; keep this
     * deployment reachable only through that proxy. */
    forwarding.KnownIPNetworks.Clear();
    forwarding.KnownProxies.Clear();

    app.UseForwardedHeaders(forwarding);
}

/* Bodies are tiny by design. Reject anything larger before it is read, and
 * refuse chunked uploads outright: no client of ours sends one. */
app.Use(async (ctx, next) =>
{
    if (HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Path.StartsWithSegments("/api/v1"))
    {
        var limit = ctx.RequestServices
            .GetRequiredService<IOptionsMonitor<LobbyOptions>>().CurrentValue.MaxBodyBytes;

        var length = ctx.Request.ContentLength;
        if (length is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status411LengthRequired;
            return;
        }
        if (length > limit)
        {
            ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
    }
    await next();
});

/* Kill switch. Two endpoints stay up while the board is off: /config, so old
 * builds learn why they should stop calling and can show the notice instead of
 * an error, and /health, which reports whether the instance is alive -- a
 * deliberately closed board is not a sick instance. */
string[] alwaysOn = ["/api/v1/config", "/api/v1/health"];

app.Use(async (ctx, next) =>
{
    var options = ctx.RequestServices
        .GetRequiredService<IOptionsMonitor<LobbyOptions>>().CurrentValue;

    if (!options.Enabled
        && ctx.Request.Path.StartsWithSegments("/api/v1")
        && !alwaysOn.Any(path => ctx.Request.Path.StartsWithSegments(path)))
    {
        await Results.Json(new ErrorResponse("disabled", options.Notice),
            statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(ctx);
        return;
    }
    await next();
});

app.UseRateLimiter();

app.MapConfigEndpoints();
app.MapRoomEndpoints();
app.MapTelemetryEndpoints();
app.MapDiagnosticsEndpoints();

app.MapGet("/", () => Results.Content(
    "<html><head><meta charset=\"utf-8\"></head><body style='font-family: sans-serif; padding: 2rem; background-color: #121212; color: #e0e0e0;'>" +
    "<h1 style='color: #ffffff;'>🚀 MAME4droid Lobby</h1>" +
    "<p>Status: <span style='color: #4ade80; font-weight: bold;'>Running</span></p>" +
    "<p>API: v1</p>" +
    $"<p>Build: <code style='color: #a0a0a0;'>{System.Net.WebUtility.HtmlEncode(BuildInfo.Version)}</code></p>" +
    $"<p>Published: <code style='color: #a0a0a0;'>{System.Net.WebUtility.HtmlEncode(BuildInfo.Published)}</code></p>" +
    "<p><em style='color: #a0a0a0;'>Rendezvous server is ready for matchmaking.</em></p>" +
    "</body></html>", "text/html; charset=utf-8")).RequireRateLimiting(RateLimitPolicies.Config);

app.Logger.LogInformation("Init MAME4droid lobby, build {Build} published {Published}",
    BuildInfo.Version, BuildInfo.Published);

app.Run();

/* Needed by WebApplicationFactory in the test project. */
public partial class Program;
