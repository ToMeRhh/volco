using Volco.App.ViewModels.Search;
using Xunit;

namespace Volco.App.Tests.ViewModels;

public class FuzzyMatcherTests
{
    [Fact]
    public void Empty_MatchesAll()
    {
        var m = FuzzyMatcher.Match("", "anything");
        Assert.NotNull(m);
        Assert.Equal(0, m.Value.Score);
        Assert.Empty(m.Value.Indexes);
    }

    [Fact]
    public void InOrder_Required() => Assert.Null(FuzzyMatcher.Match("pso", "Spotify"));

    [Fact]
    public void CaseAndSpaceIgnored() => Assert.NotNull(FuzzyMatcher.Match("PLAY PA", "Play / pause"));

    [Fact]
    public void WordStartAndRunsRankHigher()
    {
        var a = FuzzyMatcher.Match("tgo", "Toggle outputs");
        var b = FuzzyMatcher.Match("tgo", "Stop · media group");
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.True(a.Value.Score > b.Value.Score);
    }

    [Fact]
    public void Indexes() => Assert.Equal([0, 1, 2], FuzzyMatcher.Match("spo", "Spotify")!.Value.Indexes);

    [Fact]
    public void NameBeatsExtra()
    {
        var extra = FuzzyMatcher.MatchItem("mic", "Mute a device", "mic microphone");
        var name = FuzzyMatcher.MatchItem("mic", "Mic", null);
        Assert.True(extra!.Value.Score < name!.Value.Score);
    }

    [Fact]
    public void ExtraOnly_NoIndexes()
    {
        var m = FuzzyMatcher.MatchItem("microphone", "Mute a device", "mic microphone");
        Assert.NotNull(m);
        Assert.Empty(m.Value.Indexes);
    }
}
