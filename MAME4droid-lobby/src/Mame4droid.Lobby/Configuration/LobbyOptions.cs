namespace Mame4droid.Lobby.Configuration;

/// Everything tunable at runtime lives here: appsettings.json is reloaded on
/// change, so cadences and TTLs can be adjusted without publishing an APK.
/// Rate limits are the exception -- they are read once at startup.
public sealed class LobbyOptions
{
    public const string SectionName = "Lobby";

    /* Kill switch: when false only GET /config answers, everything else 503s. */
    public bool Enabled { get; set; } = true;
    public string Notice { get; set; } = "";
    public string MinApp { get; set; } = "";

    /* Client cadences, in seconds. */
    public int PollSeconds { get; set; } = 3;
    public int ListSeconds { get; set; } = 5;

    /* Adaptive list polling: the client multiplies ListSeconds by ListBackoff
     * after every 304 and clamps at ListMaxSeconds, resetting on any 200 or on
     * user interaction.  Server-driven so it can be tightened under load. */
    public double ListBackoff { get; set; } = 1.5;
    public int ListMaxSeconds { get; set; } = 20;

    /* Room lifetime. */
    public int OpenTtlSeconds { get; set; } = 60;
    public int ClaimedTtlSeconds { get; set; } = 30;

    /* Abuse limits. */

    /* Rooms one public address may hold at once. Not one per player: a house,
     * a LAN party or an office all publish from a single address, and at 2 the
     * third person to press Create was refused with nothing on screen to say
     * why. High enough for a room full of people, low enough that one address
     * cannot fill the board. */
    public int MaxRoomsPerIp { get; set; } = 8;

    public int MaxLanTuples { get; set; } = 4;
    public int MaxRoomsListed { get; set; } = 100;
    public int MaxBodyBytes { get; set; } = 1024;

    /* Wrong PINs a private room tolerates before it stops accepting joins.
     * Low on purpose: a friend mistypes once, an attacker needs thousands. */
    public int MaxPinAttempts { get; set; } = 5;

    /* Refuse a room whose advertised tuple contradicts the observed HTTP IP in
     * the same family.  Softening this to a verified:false badge is a one-line
     * config change if CGNAT pools turn out to hand different IPs per flow. */
    public bool RejectUnverified { get; set; } = true;

    /* Temporary: exposes GET /api/v1/whoami, which reports what the platform in
     * front of us says about the caller. Leave off in normal operation. */
    public bool Diagnostics { get; set; }

    /* Only for self-hosting behind your OWN reverse proxy, and only when that
     * proxy is the sole way in: it has the framework rewrite the caller from
     * X-Forwarded-For. Must stay false anywhere the host resolves the caller
     * before the app sees it -- there, one header would buy any address. */
    public bool TrustForwardedHeaders { get; set; }

    /* Sliding window used for the "N players browsing" line in the host dialog. */
    public int ViewerWindowSeconds { get; set; } = 30;

    public RateLimitOptions RateLimits { get; set; } = new();
}

/// Requests per minute, one bucket per endpoint. The bucket is keyed by public
/// address, so a household, a LAN party or an office all share a single budget
/// -- these numbers have to fit several players on one router, not one player.
/// The daily quota is defended elsewhere (kill switch, ETag/304), so these are
/// abuse ceilings, well above what normal use spends.
public sealed class RateLimitOptions
{
    public int Config { get; set; } = 30;

    /// Its own bucket: it touches no state and returns three bytes, and it is
    /// what warms a sleeping instance. Sharing Config's budget meant opening
    /// the netplay dialog could spend the board's own call.
    public int Health { get; set; } = 60;

    public int List { get; set; } = 120;
    public int Create { get; set; } = 20;
    public int Join { get; set; } = 30;
    public int Poll { get; set; } = 180;
    public int Telemetry { get; set; } = 20;
}
