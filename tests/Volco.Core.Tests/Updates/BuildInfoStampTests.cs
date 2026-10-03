using System.Reflection;
using Volco.Core.Updates;
using Xunit;

namespace Volco.Core.Tests.Updates;

/// <summary>
/// Pins Review Focus #1: after the Volco.Core split, <see cref="BuildInfo"/> lives in
/// Volco.Core and reads <see cref="Assembly.GetExecutingAssembly"/> — i.e. Volco.Core.dll, not
/// the exe. Without a root Directory.Build.props stamping every project identically, that
/// assembly carries no "ReleaseChannel" AssemblyMetadata and BuildInfo.IsRelease silently goes
/// false in every release build, hiding the in-app updater. This test fails if
/// Directory.Build.props is missing, or if it does not reach Volco.Core.
/// </summary>
public class BuildInfoStampTests
{
    [Fact]
    public void VolcoCoreAssembly_IsStampedWithReleaseChannelMetadata()
    {
        Assert.Contains(
            typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>(),
            a => a.Key == "ReleaseChannel");
    }

    [Fact]
    public void BuildInfo_BuiltAt_IsStampedOnVolcoCore()
    {
        Assert.NotNull(BuildInfo.BuiltAt);
        Assert.InRange(BuildInfo.BuiltAt!.Value, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMinutes(5));
    }

    [Fact]
    public void BuildInfo_Commit_ComesFromSourceLink()
    {
        // CI builds from a git checkout, so the SDK's SourceLink appends "+sha".
        Assert.NotNull(BuildInfo.Commit);
        Assert.StartsWith("dev ", BuildInfo.DisplayVersion);
    }

    [Fact]
    public void BuildInfo_BranchAndOrigin_AreStamped()
    {
        Assert.False(string.IsNullOrEmpty(BuildInfo.Branch));
        Assert.NotNull(BuildInfo.BuildOrigin);
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        {
            var branch = Environment.GetEnvironmentVariable("GITHUB_HEAD_REF") is { Length: > 0 } head
                ? head : Environment.GetEnvironmentVariable("GITHUB_REF_NAME");
            Assert.Equal(branch, BuildInfo.Branch);
            Assert.StartsWith("GitHub Actions: ", BuildInfo.BuildOrigin);
            Assert.EndsWith($" run #{Environment.GetEnvironmentVariable("GITHUB_RUN_NUMBER")}", BuildInfo.BuildOrigin);
        }
        else
        {
            Assert.StartsWith("Local build on ", BuildInfo.BuildOrigin);
        }
    }

    [Fact]
    public void BuildInfo_Channel_DefaultsToLocal()
    {
        Assert.Equal("local", BuildInfo.Channel);
    }
}
