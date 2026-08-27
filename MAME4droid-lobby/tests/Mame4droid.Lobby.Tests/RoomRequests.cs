namespace Mame4droid.Lobby.Tests;

/// Bodies as the Android client will send them, with helpers so a test only
/// spells out the field it is actually about.
public static class RoomRequests
{
    public static object Create(
        string publicTuple,
        string? publicAlt = null,
        string game = "mslug",
        int proto = 11,
        string[]? lan = null,
        bool sym = false,
        bool pp = true,
        bool upnp = false,
        string? country = "ES",
        string? pin = null,
        bool playing = false) => new
        {
            proto,
            app = "1.39.0",
            game,
            mode = 1,
            delay = 0,
            plugins = false,
            @public = publicTuple,
            publicAlt,
            lan = lan ?? new[] { "192.168.1.34:2080" },
            nat = new { sym, pp, upnp },
            country,
            pin,
            playing
        };

    public static object Join(
        string? publicTuple,
        int proto = 11,
        string[]? lan = null,
        bool sym = true,
        bool pp = false,
        string? country = "AR",
        string? pin = null) => new
        {
            proto,
            app = "1.39.0",
            @public = publicTuple,
            publicAlt = (string?)null,
            lan = lan ?? new[] { "192.168.1.50:2080" },
            nat = new { sym, pp, upnp = false },
            country,
            pin
        };
}

public sealed record CreateResult(string Id, string Token, int Ttl, int PollSeconds, bool Verified);

public sealed record NatResult(bool Sym, bool Pp, bool Upnp);

public sealed record SummaryResult(
    string Id, string Game, string? Country, int Mode, int Delay, bool Plugins,
    NatResult Nat, bool Verified, bool HasLan, string App, long Since, string Site,
    bool Locked, bool Playing);

public sealed record ListResult(List<SummaryResult> Rooms, int Total);

public sealed record HostResult(
    string? Public, string? PublicAlt, string[] Lan, string Game, int Mode, int Delay,
    bool Plugins, NatResult Nat, string? Country, bool Verified, bool SameSite);

public sealed record JoinResult(HostResult Host);

public sealed record PeerResult(
    string? Public, string? PublicAlt, string[] Lan, NatResult Nat, string? Country,
    bool Verified, bool SameSite);

public sealed record PollResult(string State, int Ttl, int Viewers, PeerResult? Peer);

public sealed record ConfigResult(
    bool Enabled, int PollSeconds, int ListSeconds, double ListBackoff, int ListMaxSeconds,
    string MinApp, string Notice, bool UpdateAvailable);

public sealed record ErrorResult(string Error, string? Notice);
