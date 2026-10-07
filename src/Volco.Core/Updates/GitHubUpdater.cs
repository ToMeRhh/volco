using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Volco.Core.Updates;

/// <summary>
/// Asks the public GitHub Releases API for the latest published release and its <c>Volco.exe</c>
/// asset. No authentication: the repository's releases are public.
/// </summary>
public class GitHubUpdater
{
    private const string ApiBase = "https://api.github.com";
    private const string AssetName = "Volco.exe";

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
    /// Fetch the latest published release. Returns null when there is none (GitHub answers 404)
    /// or it has no <c>Volco.exe</c> asset.
    /// </summary>
    public async Task<UpdateInfo?> GetLatestReleaseAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"{ApiBase}/repos/{_owner}/{_repo}/releases/latest", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return ParseLatestRelease(await resp.Content.ReadAsStringAsync(ct));
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
                    url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    break;
                }
            }
        }
        if (string.IsNullOrEmpty(url)) return null;

        var notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;
        return new UpdateInfo(tag!, url!, BuildInfo.ParseVersion(tag), notes);
    }
}
