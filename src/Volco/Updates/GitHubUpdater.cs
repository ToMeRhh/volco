using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Volco.Updates;

/// <summary>
/// Queries the GitHub REST API for available updates — either the latest published Release
/// (no auth required) or the artifact of the most recent successful CI build (requires a token,
/// because the Actions artifact API is authenticated even for public repositories).
/// </summary>
public class GitHubUpdater
{
    private const string ApiBase = "https://api.github.com";
    private const string AssetName = "Volco.exe";
    private const string ArtifactName = "Volco-win-x64";
    private const string WorkflowFile = "build.yml";

    private readonly HttpClient _http;
    private readonly string _owner;
    private readonly string _repo;

    public GitHubUpdater(HttpClient? http = null, string owner = BuildInfo.RepoOwner, string repo = BuildInfo.RepoName)
    {
        _http = http ?? new HttpClient();
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.TryParseAdd("Volco-Updater");
        _http.DefaultRequestHeaders.Accept.TryParseAdd("application/vnd.github+json");
        _owner = owner;
        _repo = repo;
    }

    /// <summary>
    /// Fetch the latest published release. Returns null when no usable asset is found. A token is
    /// required for private repositories (both the API call and the eventual asset download).
    /// </summary>
    public async Task<UpdateInfo?> GetLatestReleaseAsync(string token, CancellationToken ct = default)
    {
        var json = await GetAuthorizedAsync($"{ApiBase}/repos/{_owner}/{_repo}/releases/latest", token, ct);
        return ParseLatestRelease(json);
    }

    internal static UpdateInfo? ParseLatestRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(tag)) return null;

        string? url = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
                {
                    // Use the asset API URL (not browser_download_url) so the download works on
                    // private repos when fetched with Accept: application/octet-stream + a token.
                    url = a.TryGetProperty("url", out var u) ? u.GetString() : null;
                    break;
                }
            }
        }
        if (string.IsNullOrEmpty(url)) return null;

        var notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;
        return new UpdateInfo(UpdateSource.Release, tag!, url!, BuildInfo.ParseVersion(tag), RequiresExtraction: false, notes);
    }

    /// <summary>
    /// Find the artifact of the most recent successful CI build on main. Requires a GitHub token
    /// with <c>actions:read</c> scope. Returns null when no matching artifact is found.
    /// </summary>
    public async Task<UpdateInfo?> GetLatestCiArtifactAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("A GitHub token is required to download CI build artifacts.");

        var runsJson = await GetAuthorizedAsync(
            $"{ApiBase}/repos/{_owner}/{_repo}/actions/workflows/{WorkflowFile}/runs?branch=main&status=success&per_page=10",
            token, ct);

        var runId = ParseLatestRunId(runsJson);
        if (runId == null) return null;

        var artJson = await GetAuthorizedAsync(
            $"{ApiBase}/repos/{_owner}/{_repo}/actions/runs/{runId}/artifacts", token, ct);

        return ParseArtifact(artJson, runId.Value);
    }

    private async Task<string> GetAuthorizedAsync(string url, string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    internal static long? ParseLatestRunId(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("workflow_runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var run in runs.EnumerateArray())
            return run.TryGetProperty("id", out var id) ? id.GetInt64() : null;
        return null;
    }

    internal static UpdateInfo? ParseArtifact(string json, long runId)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("artifacts", out var arts) || arts.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var art in arts.EnumerateArray())
        {
            var name = art.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (!string.Equals(name, ArtifactName, StringComparison.OrdinalIgnoreCase)) continue;
            var url = art.TryGetProperty("archive_download_url", out var u) ? u.GetString() : null;
            if (string.IsNullOrEmpty(url)) return null;
            return new UpdateInfo(UpdateSource.CiArtifact, $"CI build #{runId}", url!, Version: null,
                RequiresExtraction: true, Notes: "Latest successful CI build from the main branch.");
        }
        return null;
    }
}
