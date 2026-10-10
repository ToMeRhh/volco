using Volco.Core.Communication;
using Xunit;

namespace Volco.Core.Tests.Communication;

/// <summary>ScreenIcon.g.cs is generated from firmware/assets/icons/manifest.txt; this catches a
/// manifest edit that was not regenerated (spec §5.4).</summary>
public class ScreenIconManifestTests
{
    [Fact]
    public void EnumMatchesTheManifest()
    {
        var rows = File.ReadAllLines(RepoPaths.Combine("firmware", "assets", "icons", "manifest.txt"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToList();

        Assert.Equal(rows.Count, Enum.GetValues<ScreenIcon>().Length);
        foreach (var row in rows)
            Assert.Equal(byte.Parse(row[0]), (byte)Enum.Parse<ScreenIcon>(row[2]));
    }

    [Fact]
    public void FrameCountsMatchTheManifest()
    {
        var rows = File.ReadAllLines(RepoPaths.Combine("firmware", "assets", "icons", "manifest.txt"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        foreach (var row in rows)
            Assert.Equal(int.Parse(row[3]), ScreenIconSets.FrameCount(Enum.Parse<ScreenIcon>(row[2])));
        Assert.Equal(4, ScreenIconSets.FrameCount(ScreenIcon.Equalizer));
    }
}
