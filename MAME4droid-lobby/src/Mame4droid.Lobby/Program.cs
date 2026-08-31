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
builder.Services.AddSingleton<StatsStore>();
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

app.MapGet("/", (HttpContext ctx, StatsStore store, RoomStore board,
                 IOptionsMonitor<LobbyOptions> settings) =>
{
    // This page exists to answer "which build is live and when did it wake".
    // A cached copy would lie about both, and nothing on it is worth storing.
    ctx.Response.Headers.CacheControl = "no-store";

    /* Unlike the board's showcase this holds nothing back: whoever reads this
     * page runs the service, and a threshold would only hide what they came
     * for -- games that actually got played above all. */
    var recent = store.Full();
    var activity = "<h2 style='color: #ffffff; font-size: 1.1rem; margin-top: 2.5rem;'>Recent activity</h2>";

    if (recent.Rooms == 0 && recent.Played == 0)
    {
        activity += "<p style='color: #a0a0a0;'>Nothing recorded yet.</p>";
    }
    else
    {
        activity +=
            $"<p>Rooms created: <b style='color: #ffffff;'>{recent.Rooms}</b></p>" +
            $"<p>Games played: <b style='color: #4ade80;'>{recent.Played}</b></p>";

        if (recent.Games.Count > 0)
            activity += "<p>Most wanted: <code style='color: #a0a0a0;'>" +
                System.Net.WebUtility.HtmlEncode(string.Join(", ", recent.Games)) + "</code></p>";

        if (recent.Countries > 0)
            activity += $"<p>Countries: <b style='color: #ffffff;'>{recent.Countries}</b></p>";

        activity += "<p style='color: #a0a0a0; font-size: 0.9rem;'>Since " +
            System.Net.WebUtility.HtmlEncode(recent.Since) +
            $", rolling {Math.Max(1, settings.CurrentValue.StatsWindowDays)}-day window.</p>";

        /* The whole file added up -- the counts above are cuts of this, and the
         * operator has no reason to see only the top. Capped all the same: it
         * grows with every driver anyone ever hosts. */
        var (allGames, allFinished, allCountries) = store.Totals();

        /* Wanted and Countries count a room opening AND a game finishing, so
         * they add up past the rooms figure. Said once here rather than left to
         * whoever notices the totals disagree. */
        if (allGames.Count > 0 || allCountries.Count > 0)
            activity += "<h3 style='color: #ffffff; font-size: 0.95rem; margin-top: 2rem;'>Breakdown</h3>" +
                "<p style='color: #6b6b6b; font-size: 0.8rem;'>Wanted counts every time a name " +
                "came up, a room opened and a game finished alike. Played counts only games " +
                "two people saw through.</p>";

        if (allFinished.Count > 0)
            activity += "<p style='color: #a0a0a0; font-size: 0.85rem; line-height: 1.7; " +
                "overflow-wrap: anywhere;'><b style='color: #4ade80;'>Played</b> &nbsp; " +
                Tally(allFinished) + "</p>";

        if (allGames.Count > 0)
            activity += "<p style='color: #a0a0a0; font-size: 0.85rem; line-height: 1.7; " +
                "overflow-wrap: anywhere;'><b style='color: #ffffff;'>Wanted</b> &nbsp; " +
                Tally(allGames) + "</p>";

        if (allCountries.Count > 0)
            activity += "<p style='color: #a0a0a0; font-size: 0.85rem; line-height: 1.7; " +
                "overflow-wrap: anywhere;'><b style='color: #ffffff;'>Countries</b> &nbsp; " +
                Tally(allCountries) + "</p>";
    }

    /* The board for a human rather than a client, and strictly less than
     * /api/v1/rooms already serves anyone: no address, no room id (that is the
     * join handle, which the telemetry hashes), and no site hash. */
    var live = board.ListOpen(Math.Max(1, settings.CurrentValue.MaxRoomsListed), out var waiting);
    var table = "<h2 style='color: #ffffff; font-size: 1.1rem; margin-top: 2.5rem;'>" +
                $"On the board now ({waiting})</h2>";

    if (live.Count == 0)
    {
        table += "<p style='color: #a0a0a0;'>Nobody is hosting at the moment.</p>";
    }
    else
    {
        table += "<table style='border-collapse: collapse; font-size: 0.85rem; margin-top: 0.5rem;'>" +
            "<tr style='color: #6b6b6b; text-align: left;'>" +
            "<th style='padding: 0.2rem 1.2rem 0.2rem 0;'>Game</th>" +
            "<th style='padding: 0.2rem 1.2rem 0.2rem 0;'>From</th>" +
            "<th style='padding: 0.2rem 1.2rem 0.2rem 0;'>Mode</th>" +
            "<th style='padding: 0.2rem 1.2rem 0.2rem 0;'>Delay</th>" +
            "<th style='padding: 0.2rem 1.2rem 0.2rem 0;'>Waiting</th>" +
            "<th style='padding: 0.2rem 1.2rem 0.2rem 0;'>Build</th>" +
            "<th style='padding: 0.2rem 1.2rem 0.2rem 0;'>Net</th>" +
            "<th style='padding: 0.2rem 0;'>Notes</th></tr>";

        var now = DateTimeOffset.UtcNow;

        foreach (var room in live)
        {
            /* Only what makes this room unusual. Having a local address is not:
             * every host on wi-fi publishes one, so it was true in nearly every
             * row -- and mob already marks the rare case, the host without. */
            var notes = new List<string>(2);
            if (room.Playing) notes.Add("drop-in");
            if (room.IsLocked) notes.Add("private");

            /* The same flags the telemetry lines carry, so a room on screen
             * and a session in the log read the same way. Only what is true
             * is named: a row of zeroes is harder to scan than three words. */
            var net = new List<string>(6);
            if (room.Host.Nat.Sym) net.Add("sym");
            if (room.Host.Nat.Pp) net.Add("pp");
            if (room.Host.Nat.Upnp) net.Add("upnp");
            if (room.Host.Nat.V6) net.Add("v6");
            if (room.Host.Nat.Mob) net.Add("mob");
            if (!room.Host.Verified) net.Add("unverified");

            table +=
                "<tr style='color: #e0e0e0;'>" +
                $"<td style='padding: 0.2rem 1.2rem 0.2rem 0;'><code>{System.Net.WebUtility.HtmlEncode(room.Game)}</code></td>" +
                $"<td style='padding: 0.2rem 1.2rem 0.2rem 0;'>{System.Net.WebUtility.HtmlEncode(room.Host.Country ?? "--")}</td>" +
                $"<td style='padding: 0.2rem 1.2rem 0.2rem 0;'>{(room.Mode == 1 ? "Rollback" : "Lockstep")}</td>" +
                $"<td style='padding: 0.2rem 1.2rem 0.2rem 0;'>{(room.Delay > 0 ? room.Delay.ToString() : "auto")}</td>" +
                $"<td style='padding: 0.2rem 1.2rem 0.2rem 0;'>{Elapsed(now - room.CreatedUtc)}</td>" +
                "<td style='padding: 0.2rem 1.2rem 0.2rem 0; color: #a0a0a0;'><code>" +
                System.Net.WebUtility.HtmlEncode(string.IsNullOrEmpty(room.App) ? "?" : room.App) +
                $"</code> <span style='color: #6b6b6b;'>p{room.Proto}</span></td>" +
                $"<td style='padding: 0.2rem 1.2rem 0.2rem 0; color: #a0a0a0;'>{string.Join(" ", net)}</td>" +
                $"<td style='padding: 0.2rem 0; color: #a0a0a0;'>{string.Join(" · ", notes)}</td></tr>";
        }

        table += "</table>";

        if (waiting > live.Count)
            table += $"<p style='color: #a0a0a0; font-size: 0.85rem;'>+{waiting - live.Count} more not shown.</p>";
    }

    return Results.Content(
    "<html><head><meta charset=\"utf-8\"></head><body style='font-family: sans-serif; padding: 2rem; background-color: #121212; color: #e0e0e0;'>" +
    "<h1 style='color: #ffffff;'>🚀 MAME4droid Lobby</h1>" +
    "<p>Status: <span style='color: #4ade80; font-weight: bold;'>Running</span></p>" +
    "<p>API: v1</p>" +
    $"<p>Build: <code style='color: #a0a0a0;'>{System.Net.WebUtility.HtmlEncode(BuildInfo.Version)}</code></p>" +
    $"<p>Published: <code style='color: #a0a0a0;'>{System.Net.WebUtility.HtmlEncode(BuildInfo.Published)}</code></p>" +
    // The site sleeps when nobody uses it and a request wakes it, which starts a
    // fresh process: this stamp is therefore also "last woke up". Rendered as UTC
    // and rewritten to the reader's own clock below, so it needs no converting.
    $"<p>Awake since: <code style='color: #a0a0a0;' data-utc='{BuildInfo.Started:O}'>{BuildInfo.Started:yyyy-MM-dd HH:mm:ss} UTC</code> " +
    $"<span style='color: #a0a0a0;'>({System.Net.WebUtility.HtmlEncode(BuildInfo.UptimeText)})</span></p>" +
    activity + table +
    "<p style='margin-top: 2.5rem;'><em style='color: #a0a0a0;'>Rendezvous server is ready for matchmaking.</em></p>" +
    "<script>for (const el of document.querySelectorAll('code[data-utc]')) {" +
    " const d = new Date(el.dataset.utc); if (!isNaN(d)) el.textContent = d.toLocaleString(); }</script>" +
    "</body></html>", "text/html; charset=utf-8");
}).RequireRateLimiting(RateLimitPolicies.Config);

/* Resolved before the first request so the day buckets are read once at boot,
 * and the line below is followed by what it found. Written back on the way out
 * because idling out of memory is this instance's normal end, not a crash. */
var stats = app.Services.GetRequiredService<StatsStore>();
app.Lifetime.ApplicationStopping.Register(stats.Flush);

app.Logger.LogInformation("Init MAME4droid lobby, build {Build} published {Published}",
    BuildInfo.Version, BuildInfo.Published);

app.Run();

/// How long a room has been up, in the largest unit that still says something.
/// Rendered here rather than sent as a timestamp: this page is never cached,
/// so unlike the room listing there is no ETag for a ticking value to spoil.
static string Elapsed(TimeSpan waited)
{
    if (waited < TimeSpan.Zero) waited = TimeSpan.Zero;

    if (waited.TotalMinutes < 1) return $"{(int)waited.TotalSeconds}s";
    if (waited.TotalHours < 1) return $"{(int)waited.TotalMinutes}m";
    return $"{(int)waited.TotalHours}h {waited.Minutes}m";
}

/// A name with its count, busiest first, as one wrapped line. No flag emoji
/// here unlike the app: this page is read in a desktop browser, and Windows
/// draws a flag as the two letters it is made of -- so it said the code twice.
static string Tally(IReadOnlyList<KeyValuePair<string, int>> counts)
{
    const int Shown = 250;

    var line = string.Join(" &nbsp; ", counts.Take(Shown).Select(entry =>
        System.Net.WebUtility.HtmlEncode(entry.Key) +
        $" <span style='color: #6b6b6b;'>{entry.Value}</span>"));

    var rest = counts.Count - Shown;
    return rest > 0 ? line + $" &nbsp; <em>+{rest} more</em>" : line;
}

/* Needed by WebApplicationFactory in the test project. */
public partial class Program;
