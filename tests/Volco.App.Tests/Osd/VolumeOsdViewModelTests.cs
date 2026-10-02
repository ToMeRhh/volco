using Volco.App.Osd;
using Volco.Core.Input;
using Xunit;

namespace Volco.App.Tests.Osd;

public class VolumeOsdViewModelTests
{
    [Fact]
    public void OsdViewModel_VolumeKind_ShowsRoundedPercent()
    {
        var vm = new VolumeOsdViewModel();
        vm.Apply(new VolumeChangeNotification("Spotify", 0.374f, false, OsdKind.Volume));

        Assert.Equal("37%", vm.ValueText);
        Assert.Equal("Spotify", vm.Label);
        Assert.Equal(37.4, vm.Level, 1);
        Assert.NotEqual(OsdGlyph.Muted, vm.Glyph);
    }

    [Fact]
    public void OsdViewModel_MuteKind_ShowsMutedOrPercent()
    {
        var vm = new VolumeOsdViewModel();

        vm.Apply(new VolumeChangeNotification("Output", 0.42f, true, OsdKind.Mute));
        Assert.Equal("Muted", vm.ValueText);   // never a percentage for a muted device
        Assert.Equal(OsdGlyph.Muted, vm.Glyph);
        Assert.Equal(42, vm.Level, 1);         // the bar keeps the real level...
        Assert.Equal(0.4, vm.LevelBarOpacity); // ...dimmed

        vm.Apply(new VolumeChangeNotification("Output", 0.42f, false, OsdKind.Mute));
        Assert.Equal("42%", vm.ValueText);     // unmute shows the level, not "On"
        Assert.NotEqual(OsdGlyph.Muted, vm.Glyph);
        Assert.Equal(1.0, vm.LevelBarOpacity);
    }

    [Fact]
    public void OsdViewModel_VolumeKindWhileMuted_ShowsPercentWithMutedGlyph()
    {
        var vm = new VolumeOsdViewModel();
        vm.Apply(new VolumeChangeNotification("Output", 0.5f, true, OsdKind.Volume));

        Assert.Equal("50%", vm.ValueText);      // a drag shows the level being set...
        Assert.Equal(OsdGlyph.Muted, vm.Glyph); // ...while the glyph...
        Assert.Equal(50, vm.Level, 1);
        Assert.Equal(0.4, vm.LevelBarOpacity);  // ...and the dimmed bar show the mute
    }

    [Fact]
    public void OsdViewModel_LongLabel_IsPassedThroughForEllipsis()
    {
        var vm = new VolumeOsdViewModel();
        var label = new string('x', 200);
        vm.Apply(new VolumeChangeNotification(label, 0.2f, false, OsdKind.Volume));

        Assert.Equal(label, vm.Label);   // trimming is the view's job (TextTrimming)
    }

    [Fact]
    public void OsdViewModel_Apply_ReportsChangeOnlyWhenSomethingChanged()
    {
        var vm = new VolumeOsdViewModel();
        var n = new VolumeChangeNotification("A", 0.3f, false, OsdKind.Volume);

        Assert.True(vm.Apply(n));
        Assert.False(vm.Apply(n));
        Assert.True(vm.Apply(n with { Level = 0.31f }));
    }

    [Fact]
    public void OsdViewModel_OutOfRangeLevel_IsClamped()
    {
        var vm = new VolumeOsdViewModel();
        vm.Apply(new VolumeChangeNotification("A", 1.4f, false, OsdKind.Volume));
        Assert.Equal(100, vm.Level);
        Assert.Equal("100%", vm.ValueText);
    }

    [Fact]
    public void OsdViewModel_DeviceKind_UsesSpeakerGlyphAndPercent()
    {
        var vm = new VolumeOsdViewModel();
        vm.Apply(new VolumeChangeNotification("Headphones", 0.37f, false, OsdKind.Device));

        Assert.Equal(OsdGlyph.Speaker, vm.Glyph);
        Assert.Equal("37%", vm.ValueText);
        Assert.Equal("Headphones", vm.Label);
        Assert.Equal(1.0, vm.LevelBarOpacity);
    }

    [Fact]
    public void OsdViewModel_DeviceKindMuted_ShowsMutedTextAndMutedGlyph()
    {
        var vm = new VolumeOsdViewModel();
        vm.Apply(new VolumeChangeNotification("Headphones", 0.37f, true, OsdKind.Device));

        Assert.Equal(OsdGlyph.Muted, vm.Glyph);
        Assert.Equal("Muted", vm.ValueText);
        Assert.Equal(37, vm.Level, 1);
        Assert.Equal(0.4, vm.LevelBarOpacity);
    }

    [Fact]
    public void OsdViewModel_Glyph_FollowsVolumeAndMuteKinds()
    {
        var vm = new VolumeOsdViewModel();
        vm.Apply(new VolumeChangeNotification("A", 0.5f, false, OsdKind.Volume));
        Assert.Equal(OsdGlyph.Volume, vm.Glyph);
        vm.Apply(new VolumeChangeNotification("A", 0.5f, true, OsdKind.Volume));
        Assert.Equal(OsdGlyph.Muted, vm.Glyph);
        vm.Apply(new VolumeChangeNotification("A", 0.5f, false, OsdKind.Mute));
        Assert.Equal(OsdGlyph.Volume, vm.Glyph);
        vm.Apply(new VolumeChangeNotification("A", 0.5f, false, OsdKind.Device));
        // Same label/level/text, only the glyph differs: still reported as a change.
        Assert.True(vm.Apply(new VolumeChangeNotification("A", 0.5f, false, OsdKind.Volume)));
        Assert.Equal(OsdGlyph.Volume, vm.Glyph);
    }

    [Fact]
    public void OsdViewModel_PresetKind_ShowsTheNameAndPreset_WithoutALevel()
    {
        var vm = new VolumeOsdViewModel();

        vm.Apply(new VolumeChangeNotification("Gaming", 0f, false, OsdKind.Preset));

        Assert.Equal("Gaming", vm.Label);
        Assert.Equal("Preset", vm.ValueText);
        Assert.Equal(OsdGlyph.Preset, vm.Glyph);
        Assert.False(vm.ShowsLevel);
    }

    [Fact]
    public void OsdViewModel_VolumeAfterPreset_ShowsTheLevelAgain()
    {
        var vm = new VolumeOsdViewModel();
        vm.Apply(new VolumeChangeNotification("Gaming", 0f, false, OsdKind.Preset));

        vm.Apply(new VolumeChangeNotification("Spotify", 0.5f, false, OsdKind.Volume));

        Assert.True(vm.ShowsLevel);
        Assert.Equal("50%", vm.ValueText);
        Assert.Equal(OsdGlyph.Volume, vm.Glyph);
    }
}
