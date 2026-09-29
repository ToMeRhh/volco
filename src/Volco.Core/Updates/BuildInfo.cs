using System.Reflection;

namespace Volco.Core.Updates;

/// <summary>
/// Exposes the version and release channel baked into the assembly at build time.
/// Release builds are stamped by the release CI workflow (ReleaseChannel=release plus a real
/// version derived from the git tag); local/dev builds fall back to the "local" channel and a
/// 0.0.0 version. The app uses <see cref="IsRelease"/> to decide whether to show the in-app
/// updater — local builds shouldn't try to overwrite a developer's working copy.
/// </summary>
public static class BuildInfo
{
    /// <summary>GitHub repository owner used for update checks.</summary>
    public const string RepoOwner = "ToMeRhh";

    /// <summary>GitHub repository name used for update checks.</summary>
    public const string RepoName = "volco";

    private static readonly Lazy<(Version Version, string Channel)> Info = new(Read);

    /// <summary>The build's semantic version (e.g. 1.2.3). 0.0.0 for un-stamped local builds.</summary>
    public static Version Version => Info.Value.Version;

    /// <summary>Release channel: "release" for published builds, "local" otherwise.</summary>
    public static string Channel => Info.Value.Channel;

    /// <summary>True when this is an official release build (vs a local/dev build).</summary>
    public static bool IsRelease => string.Equals(Channel, "release", StringComparison.OrdinalIgnoreCase);

    /// <summary>Human-friendly version string, e.g. "v1.2.3" or "v0.0.0 (local)".</summary>
    public static string DisplayVersion => IsRelease ? $"v{Version}" : $"v{Version} ({Channel})";

    private static (Version, string) Read()
    {
        var asm = Assembly.GetExecutingAssembly();

        var channel = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "ReleaseChannel")?.Value;

        // InformationalVersion may carry a "+metadata" suffix; ParseVersion strips it.
        var infoVersion = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = ParseVersion(infoVersion) ?? asm.GetName().Version ?? new Version(0, 0, 0);

        return (version, string.IsNullOrWhiteSpace(channel) ? "local" : channel!);
    }

    /// <summary>
    /// Parse a version out of a tag or informational-version string, tolerating a leading "v"
    /// and any "-prerelease"/"+build" suffix. Returns null when no numeric version is present.
    /// </summary>
    internal static Version? ParseVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) s = s[..cut];
        return Version.TryParse(s, out var v) ? v : null;
    }
}
