using System.Globalization;
using System.Reflection;

namespace Volco.Core.Updates;

/// <summary>
/// Exposes the version and release channel baked into the assembly at build time.
/// Release builds are stamped by the release CI workflow (ReleaseChannel=release plus a real
/// version derived from the git tag); local/dev builds fall back to the "local" channel and a
/// 0.0.0 version. Every build also carries its branch, commit, build time and origin (a GitHub
/// Actions run or a local machine), so a dev build can still be told apart in the UI and in bug
/// reports. The app uses <see cref="IsRelease"/> to decide whether
/// to show the in-app updater — local builds shouldn't try to overwrite a developer's working copy.
/// </summary>
public static class BuildInfo
{
    /// <summary>GitHub repository owner used for update checks.</summary>
    public const string RepoOwner = "ToMeRhh";

    /// <summary>GitHub repository name used for update checks.</summary>
    public const string RepoName = "volco";

    /// <summary>The project's page on GitHub, linked from the General page.</summary>
    public const string RepoUrl = $"https://github.com/{RepoOwner}/{RepoName}";

    private sealed record Stamp(Version Version, string Channel, string? Commit, string? Branch, DateTimeOffset? BuiltAt, string? Origin);

    private static readonly Lazy<Stamp> Info = new(Read);

    /// <summary>The build's semantic version (e.g. 1.2.3). 0.0.0 for un-stamped local builds.</summary>
    public static Version Version => Info.Value.Version;

    /// <summary>Release channel: "release" for published builds, "local" otherwise.</summary>
    public static string Channel => Info.Value.Channel;

    /// <summary>True when this is an official release build (vs a local/dev build).</summary>
    public static bool IsRelease => string.Equals(Channel, "release", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Short commit the build was made from (7 hex digits), or null when unknown. The SDK's
    /// built-in SourceLink appends the full SHA to InformationalVersion as "+sha".
    /// </summary>
    public static string? Commit => Info.Value.Commit;

    /// <summary>The branch (or tag) the build was made from; null when unknown or detached.</summary>
    public static string? Branch => Info.Value.Branch;

    /// <summary>When the build ran, from Volco.Core's "BuildTimestamp" metadata; null when unknown.</summary>
    public static DateTimeOffset? BuiltAt => Info.Value.BuiltAt;

    /// <summary>
    /// How the build was made, e.g. "GitHub Actions: Build Volco run #42" or "Local build on PC";
    /// null when unknown.
    /// </summary>
    public static string? BuildOrigin => Info.Value.Origin;

    /// <summary>
    /// Human-friendly version string: "v1.2.3" for releases; for other builds, which all carry
    /// 0.0.0, "dev" plus the commit (e.g. "dev a1b2c3d").
    /// </summary>
    public static string DisplayVersion => FormatVersion(IsRelease, Version, Commit);

    /// <summary>
    /// The branch, commit, build time and origin, one per line, e.g.
    /// "Branch main · commit a1b2c3d\nBuilt 2026-10-03 14:22 UTC\nLocal build on PC"; null when
    /// nothing is known. Every build has these; the UI shows them on non-release builds only.
    /// </summary>
    public static string? BuildDetails => FormatDetails(Branch, Commit, BuiltAt, BuildOrigin);

    public static string FormatVersion(bool isRelease, Version version, string? commit)
        => isRelease ? $"v{version}" : commit is null ? "dev" : $"dev {commit}";

    internal static string? FormatDetails(string? branch, string? commit, DateTimeOffset? builtAt, string? origin)
    {
        var source = (branch, commit) switch
        {
            (not null, not null) => $"Branch {branch} · commit {commit}",
            (not null, null) => $"Branch {branch}",
            (null, not null) => $"Commit {commit}",
            _ => null,
        };
        var time = builtAt?.UtcDateTime.ToString("'Built 'yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        var lines = new[] { source, time, origin }.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static Stamp Read()
    {
        var asm = Assembly.GetExecutingAssembly();

        var metadata = asm.GetCustomAttributes<AssemblyMetadataAttribute>().ToList();
        var channel = metadata.FirstOrDefault(a => a.Key == "ReleaseChannel")?.Value;
        var stamp = metadata.FirstOrDefault(a => a.Key == "BuildTimestamp")?.Value;
        DateTimeOffset? builtAt = DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var t) ? t : null;
        // A detached checkout reports its branch as "HEAD", which says nothing.
        var branch = metadata.FirstOrDefault(a => a.Key == "BuildBranch")?.Value;
        if (string.IsNullOrWhiteSpace(branch) || branch == "HEAD") branch = null;
        var origin = metadata.FirstOrDefault(a => a.Key == "BuildOrigin")?.Value;
        if (string.IsNullOrWhiteSpace(origin)) origin = null;

        // InformationalVersion may carry a "+metadata" suffix; ParseVersion strips it.
        var infoVersion = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = ParseVersion(infoVersion) ?? asm.GetName().Version ?? new Version(0, 0, 0);

        return new Stamp(version, string.IsNullOrWhiteSpace(channel) ? "local" : channel!,
            ParseCommit(infoVersion), branch, builtAt, origin);
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

    /// <summary>
    /// The short commit from an informational version's "+sha" suffix ("1.2.3+a1b2c3d4…" gives
    /// "a1b2c3d"). Returns null when there is no suffix or it isn't a hex SHA.
    /// </summary>
    internal static string? ParseCommit(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var plus = raw.IndexOf('+');
        if (plus < 0) return null;
        var sha = raw[(plus + 1)..].Trim();
        if (sha.Length < 7 || !sha.All(Uri.IsHexDigit)) return null;
        return sha[..7].ToLowerInvariant();
    }
}
