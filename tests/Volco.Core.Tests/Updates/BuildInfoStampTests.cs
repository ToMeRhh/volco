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
    public void BuildInfo_Channel_DefaultsToLocal()
    {
        Assert.Equal("local", BuildInfo.Channel);
    }
}
