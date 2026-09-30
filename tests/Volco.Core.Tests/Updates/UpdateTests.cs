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
        Assert.Equal(UpdateSource.Release, info!.Source);
        Assert.Equal("v1.5.0", info.DisplayName);
        // Uses the asset API URL (works on private repos with octet-stream + token).
        Assert.Equal("https://api/assets/123", info.DownloadUrl);
        Assert.Equal(new Version(1, 5, 0), info.Version);
        Assert.False(info.RequiresExtraction);
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

    [Fact]
    public void ParseLatestRunId_ReturnsFirstRun()
    {
        var json = """
        { "workflow_runs": [ { "id": 987654321 }, { "id": 1 } ] }
        """;
        Assert.Equal(987654321L, GitHubUpdater.ParseLatestRunId(json));
    }

    [Fact]
    public void ParseLatestRunId_ReturnsNull_WhenEmpty()
        => Assert.Null(GitHubUpdater.ParseLatestRunId("""{ "workflow_runs": [] }"""));

    [Fact]
    public void ParseArtifact_FindsMatchingArtifact()
    {
        var json = """
        {
          "artifacts": [
            { "name": "other", "archive_download_url": "https://example/other.zip" },
            { "name": "Volco-win-x64", "archive_download_url": "https://example/volco.zip" }
          ]
        }
        """;

        var info = GitHubUpdater.ParseArtifact(json, 42);

        Assert.NotNull(info);
        Assert.Equal(UpdateSource.CiArtifact, info!.Source);
        Assert.Equal("CI build #42", info.DisplayName);
        Assert.Equal("https://example/volco.zip", info.DownloadUrl);
        Assert.True(info.RequiresExtraction);
        Assert.Null(info.Version);
    }

    [Fact]
    public void ParseArtifact_ReturnsNull_WhenNoMatch()
    {
        var json = """{ "artifacts": [ { "name": "nope", "archive_download_url": "u" } ] }""";
        Assert.Null(GitHubUpdater.ParseArtifact(json, 42));
    }
}
