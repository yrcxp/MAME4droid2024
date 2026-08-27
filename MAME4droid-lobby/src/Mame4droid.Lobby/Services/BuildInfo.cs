using System.Reflection;

namespace Mame4droid.Lobby.Services;

/// Which build is actually running, printed on the home page and on the first
/// line of the log. The stamp is baked into the assembly at compile time, so a
/// stale package deployed by mistake shows the OLD value -- which is the whole
/// point: it answers "is what I just uploaded the thing serving requests".
public static class BuildInfo
{
    public static string Version { get; } = ReadVersion();

    /// When the package was produced, from the build.txt the publish step
    /// leaves beside the binaries. The same file sits in the local publish
    /// folder, so the two can be compared without guessing.
    public static string Published { get; } = ReadPublished();

    private static string ReadVersion()
    {
        var stamped = typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return string.IsNullOrWhiteSpace(stamped) ? "dev" : stamped;
    }

    private static string ReadPublished()
    {
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "build.txt");
            foreach (var line in File.Exists(file) ? File.ReadAllLines(file) : [])
                if (line.StartsWith("published=", StringComparison.Ordinal))
                    return line["published=".Length..].Trim();
        }
        catch
        {
            /* Running from a plain build, or the file is unreadable: the
             * assembly stamp above still names the build. */
        }
        return "unknown";
    }
}
