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

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mame4droid.Lobby.Tests;

/// In-process host. The server identifies a caller by the connection's remote
/// address, so tests set exactly that: the test host leaves it null, and a
/// header would prove nothing, since believing headers is the bug these tests
/// exist to prevent.
public sealed class LobbyFactory : WebApplicationFactory<Program>
{
    public const string ClientIpHeader = "X-Test-Client-Ip";

    private readonly Dictionary<string, string?> _settings;

    public LobbyFactory(params (string Key, string Value)[] settings)
    {
        _settings = settings.ToDictionary(s => "Lobby:" + s.Key, s => (string?)s.Value);

        /* Limits are per caller and the suite reuses a handful of addresses, so
         * lift them unless a test is specifically about throttling. */
        foreach (var policy in new[] { "Config", "Health", "List", "Create", "Join", "Poll", "Telemetry" })
            _settings.TryAdd($"Lobby:RateLimits:{policy}", "10000");
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        /* Not Development: the dev settings file relaxes checks for local runs,
         * and the suite must see the defaults the deployment will run with. */
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(_settings));
        builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter, TestClientAddress>());
    }

    public HttpClient CallerFrom(string ip)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(ClientIpHeader, ip);
        return client;
    }

    /// Test-only: puts the requested address on the connection itself, ahead of
    /// everything the app registers.
    private sealed class TestClientAddress : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, continuation) =>
            {
                if (ctx.Request.Headers.TryGetValue(ClientIpHeader, out var raw)
                    && IPAddress.TryParse(raw.ToString(), out var ip))
                    ctx.Connection.RemoteIpAddress = ip;

                await continuation();
            });
            next(app);
        };
    }
}

public static class LobbyHttp
{
    public static Task<HttpResponseMessage> PostJson<T>(this HttpClient client, string url, T body)
        => client.PostAsJsonAsync(url, body, LobbyFactory.Json);

    public static async Task<T> Read<T>(this HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<T>(LobbyFactory.Json))!;
}
