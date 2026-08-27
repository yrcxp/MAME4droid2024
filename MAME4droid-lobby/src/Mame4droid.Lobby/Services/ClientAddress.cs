using System.Net;

namespace Mame4droid.Lobby.Services;

/// Resolves who is actually calling, which the anti-reflection check and every
/// rate-limit bucket depend on.
///
/// Only the connection's remote address is trusted. The host resolves the
/// caller before the app sees the request, so anything left in X-Forwarded-For
/// or X-Client-IP by then is what the caller typed -- measured on the live
/// deployment, and reading it would hand anyone a victim's address to publish
/// as verified and a way to shed rate limits by rotating a header.
public static class ClientAddress
{
    public static IPAddress Resolve(HttpContext ctx)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        return remote is null ? IPAddress.None : Normalise(remote);
    }

    /// Rate-limit bucket. IPv6 is bucketed per /64 because a single subscriber
    /// gets a whole prefix and could otherwise rotate addresses for free.
    public static string PartitionKey(IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            return ip.ToString();

        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    /// Same address, whatever form each transport reported it in. Exactness
    /// matters here: this is what the anti-reflection check rests on.
    public static bool SameHost(IPAddress? a, IPAddress? b)
        => a is not null && b is not null && Normalise(a).Equals(Normalise(b));

    /// Behind the same router, which is a wider question than the same
    /// address: two devices in one house share a v4 address through NAT, but
    /// over IPv6 they get different addresses inside one /64. Comparing
    /// exactly would tell an IPv6 LAN pair they are strangers and send them
    /// out over the internet to reach the next room.
    public static bool SameSite(IPAddress? a, IPAddress? b)
        => a is not null && b is not null
           && PartitionKey(Normalise(a)) == PartitionKey(Normalise(b));

    private static IPAddress Normalise(IPAddress ip)
        => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}
