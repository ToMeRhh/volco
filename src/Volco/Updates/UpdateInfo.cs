namespace Volco.Updates;

/// <summary>Where an available update originates from.</summary>
public enum UpdateSource
{
    /// <summary>A published GitHub Release asset (no authentication required).</summary>
    Release,

    /// <summary>An artifact from the latest successful CI run (requires a GitHub token).</summary>
    CiArtifact
}

/// <summary>
/// Describes a downloadable update and how to apply it.
/// </summary>
/// <param name="Source">Origin of the payload.</param>
/// <param name="DisplayName">Label shown to the user (e.g. "v1.3.0" or "CI build #1234").</param>
/// <param name="DownloadUrl">URL to fetch the payload from.</param>
/// <param name="Version">Parsed semantic version, when known (releases only).</param>
/// <param name="RequiresExtraction">True when the download is a zip that wraps the exe (CI artifacts).</param>
/// <param name="Notes">Optional release notes / description.</param>
public record UpdateInfo(
    UpdateSource Source,
    string DisplayName,
    string DownloadUrl,
    Version? Version,
    bool RequiresExtraction,
    string? Notes = null);
