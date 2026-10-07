namespace Volco.Core.Updates;

/// <summary>
/// The latest published GitHub Release and where to download its <c>Volco.exe</c>.
/// </summary>
/// <param name="DisplayName">The release's tag, shown to the user (e.g. "v1.3.0").</param>
/// <param name="DownloadUrl">The asset's public download URL.</param>
/// <param name="Version">The version parsed from the tag, when it has one.</param>
/// <param name="Notes">The release notes, if any.</param>
public record UpdateInfo(
    string DisplayName,
    string DownloadUrl,
    Version? Version,
    string? Notes = null);
