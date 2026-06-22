using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Volco.Updates;

/// <summary>
/// Downloads an update payload and applies it in place. Because a running single-file exe can't
/// overwrite itself, we stage the new exe under a temp folder and hand off to a small batch
/// script that waits for this process to exit, swaps the file, and relaunches the app.
/// </summary>
public class UpdateInstaller
{
    private static readonly string StagingDir = Path.Combine(Path.GetTempPath(), "Volco.Update");

    private readonly HttpClient _http;

    public UpdateInstaller(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.TryParseAdd("Volco-Updater");
    }

    /// <summary>
    /// Download <paramref name="info"/> and stage the new exe. Returns the path to the staged
    /// Volco.exe, ready to be swapped in by <see cref="ApplyAndRestart"/>.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo info, string? token,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (Directory.Exists(StagingDir)) Directory.Delete(StagingDir, recursive: true);
        Directory.CreateDirectory(StagingDir);

        var downloadPath = Path.Combine(StagingDir, info.RequiresExtraction ? "payload.zip" : "Volco.exe");

        using var req = new HttpRequestMessage(HttpMethod.Get, info.DownloadUrl);
        if (info.Source == UpdateSource.CiArtifact && !string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(downloadPath))
        {
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total is > 0) progress?.Report((double)read / total.Value);
            }
        }

        if (!info.RequiresExtraction)
            return downloadPath;

        // CI artifact zips wrap the published files; pull Volco.exe out of it.
        var extractDir = Path.Combine(StagingDir, "extracted");
        ZipFile.ExtractToDirectory(downloadPath, extractDir);
        return Directory.EnumerateFiles(extractDir, "Volco.exe", SearchOption.AllDirectories).FirstOrDefault()
               ?? throw new InvalidOperationException("Volco.exe not found in the downloaded artifact.");
    }

    /// <summary>
    /// Launch a detached helper that waits for this process to exit, replaces the running exe
    /// with <paramref name="stagedExe"/>, and relaunches. The caller must exit immediately after.
    /// </summary>
    public static void ApplyAndRestart(string stagedExe)
    {
        var current = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine the current executable path.");
        var pid = Environment.ProcessId;

        var script = Path.Combine(StagingDir, "apply.bat");
        File.WriteAllText(script, $"""
            @echo off
            :wait
            tasklist /FI "PID eq {pid}" 2>NUL | find "{pid}" >NUL
            if not errorlevel 1 (
                ping -n 2 127.0.0.1 >NUL
                goto wait
            )
            move /Y "{stagedExe}" "{current}" >NUL
            start "" "{current}"
            del "%~f0"
            """);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{script}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }
}
