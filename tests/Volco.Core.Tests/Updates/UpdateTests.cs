using System.Net;
using Volco.Core.Updates;
using Xunit;

namespace Volco.Core.Tests.Updates;

public class UpdateTests
{
    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("V2.0.0", 2, 0, 0)]
    [InlineData("1.4.0-beta", 1, 4, 0)]
    [InlineData("1.4.0+abcdef", 1, 4, 0)]
    [InlineData("3.1", 3, 1, -1)]
    public void ParseVersion_HandlesCommonTagShapes(string raw, int major, int minor, int build)
    {
        var v = BuildInfo.ParseVersion(raw);
        Assert.NotNull(v);
        Assert.Equal(major, v!.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(build, v.Build);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-version")]
    public void ParseVersion_ReturnsNull_ForJunk(string? raw)
        => Assert.Null(BuildInfo.ParseVersion(raw));

    [Theory]
    [InlineData("0.0.0+A1B2C3D4E5F60718293a4b5c6d7e8f9012345678", "a1b2c3d")]
    [InlineData("1.2.3+a1b2c3d", "a1b2c3d")]
    [InlineData("1.2.3", null)]
    [InlineData("1.2.3+abc", null)]
    [InlineData("1.2.3+not-a-sha", null)]
    [InlineData(null, null)]
    public void ParseCommit_TakesTheShortShaFromTheSuffix(string? raw, string? expected)
        => Assert.Equal(expected, BuildInfo.ParseCommit(raw));

    [Fact]
    public void FormatVersion_ReleaseShowsTheVersion_OtherBuildsShowTheCommit()
    {
        Assert.Equal("v1.2.3", BuildInfo.FormatVersion(true, new Version(1, 2, 3), "a1b2c3d"));
        Assert.Equal("dev a1b2c3d", BuildInfo.FormatVersion(false, new Version(0, 0, 0), "a1b2c3d"));
        Assert.Equal("dev", BuildInfo.FormatVersion(false, new Version(0, 0, 0), null));
    }

    [Fact]
    public void FormatDetails_JoinsWhateverIsKnown()
    {
        var t = new DateTimeOffset(2026, 10, 3, 16, 22, 59, TimeSpan.FromHours(2));
        Assert.Equal("Branch main · commit a1b2c3d\nBuilt 2026-10-03 14:22 UTC\nLocal build on PC",
            BuildInfo.FormatDetails("main", "a1b2c3d", t, "Local build on PC"));
        Assert.Equal("Commit a1b2c3d\nGitHub Actions: Build Volco run #7",
            BuildInfo.FormatDetails(null, "a1b2c3d", null, "GitHub Actions: Build Volco run #7"));
        Assert.Equal("Branch main\nBuilt 2026-10-03 14:22 UTC", BuildInfo.FormatDetails("main", null, t, null));
        Assert.Null(BuildInfo.FormatDetails(null, null, null, null));
    }

    [Fact]
    public void ParseLatestRelease_FindsExeAsset()
    {
        var json = """
        {
          "tag_name": "v1.5.0",
          "body": "Release notes here",
          "assets": [
            { "name": "checksums.txt", "url": "https://api/checksums", "browser_download_url": "https://example/checksums.txt" },
            { "name": "Volco.exe", "url": "https://api/assets/123", "browser_download_url": "https://example/Volco.exe" }
          ]
        }
        """;

        var info = GitHubUpdater.ParseLatestRelease(json);

        Assert.NotNull(info);
        Assert.Equal("v1.5.0", info!.DisplayName);
        // The public download URL, which needs no token.
        Assert.Equal("https://example/Volco.exe", info.DownloadUrl);
        Assert.Equal(new Version(1, 5, 0), info.Version);
        Assert.Equal("Release notes here", info.Notes);
    }

    [Fact]
    public void ParseLatestRelease_ReturnsNull_WhenNoExeAsset()
    {
        var json = """
        { "tag_name": "v1.5.0", "assets": [ { "name": "readme.txt", "browser_download_url": "u" } ] }
        """;
        Assert.Null(GitHubUpdater.ParseLatestRelease(json));
    }

    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task GetLatestRelease_AsksThePublicApi_WithoutAToken()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """
        { "tag_name": "v2.0.0", "assets": [ { "name": "Volco.exe", "browser_download_url": "https://example/Volco.exe" } ] }
        """);

        var info = await new GitHubUpdater(new HttpClient(handler), "owner", "repo").GetLatestReleaseAsync();

        Assert.Equal("https://api.github.com/repos/owner/repo/releases/latest", handler.Request!.RequestUri!.ToString());
        Assert.Null(handler.Request.Headers.Authorization);
        Assert.Equal(new Version(2, 0, 0), info!.Version);
    }

    [Fact]
    public async Task GetLatestRelease_ReturnsNull_WhenThereIsNoRelease()
    {
        var handler = new CapturingHandler(HttpStatusCode.NotFound, """{ "message": "Not Found" }""");
        Assert.Null(await new GitHubUpdater(new HttpClient(handler)).GetLatestReleaseAsync());
    }
}
