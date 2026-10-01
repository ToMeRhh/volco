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
        Assert.False(vm.IsMutedGlyph);
    }

    [Fact]
    public void OsdViewModel_MuteKind_ShowsMutedOrOn()
    {
        var vm = new VolumeOsdViewModel();

        vm.Apply(new VolumeChangeNotification("Output", 1.0f, true, OsdKind.Mute));
        Assert.Equal("Muted", vm.ValueText);   // never "100%" for a muted device
        Assert.True(vm.IsMutedGlyph);

        vm.Apply(new VolumeChangeNotification("Output", 1.0f, false, OsdKind.Mute));
        Assert.Equal("On", vm.ValueText);
        Assert.False(vm.IsMutedGlyph);
    }

    [Fact]
    public void OsdViewModel_VolumeKindWhileMuted_ShowsPercentWithMutedGlyph()
    {
        var vm = new VolumeOsdViewModel();
        vm.Apply(new VolumeChangeNotification("Output", 0.5f, true, OsdKind.Volume));

        Assert.Equal("50%", vm.ValueText);
        Assert.True(vm.IsMutedGlyph);
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
}
