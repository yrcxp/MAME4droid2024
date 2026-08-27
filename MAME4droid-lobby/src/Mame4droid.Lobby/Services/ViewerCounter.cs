using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Mame4droid.Lobby.Configuration;
using Microsoft.Extensions.Options;

namespace Mame4droid.Lobby.Services;

/// Feeds the "N players browsing" line in the host's waiting dialog: how many
/// distinct callers listed the board recently. Callers are keyed by a salted
/// hash generated at startup, so nothing here can be turned back into an IP or
/// correlated across restarts.
public sealed class ViewerCounter
{
    private readonly IOptionsMonitor<LobbyOptions> _options;
    private readonly ConcurrentDictionary<string, long> _seen = new(StringComparer.Ordinal);
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(16);

    public ViewerCounter(IOptionsMonitor<LobbyOptions> options) => _options = options;

    public void Touch(IPAddress ip)
        => _seen[Hash(ip)] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public int Count()
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                     - _options.CurrentValue.ViewerWindowSeconds;

        var live = 0;
        foreach (var pair in _seen)
        {
            if (pair.Value >= cutoff) live++;
            else _seen.TryRemove(pair);
        }
        return live;
    }

    private string Hash(IPAddress ip)
    {
        var data = Encoding.UTF8.GetBytes(ClientAddress.PartitionKey(ip));
        return Convert.ToHexString(HMACSHA256.HashData(_salt, data), 0, 8);
    }
}
