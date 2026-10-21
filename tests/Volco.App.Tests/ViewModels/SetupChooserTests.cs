using Volco.App.ViewModels;
using Volco.App.ViewModels.Hardware;
using Volco.Core.Communication;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Volco.Core.Tests.Runtime;
using Xunit;

namespace Volco.App.Tests.ViewModels;

/// <summary>Hardware-page spec §7.1: the setup chooser's cards, when it shows, and what a pick does.</summary>
public sealed class SetupChooserTests : IDisposable
{
    private readonly TestRig _rig = new();
    private readonly RecordingDispatcher _ui = new();
    private SettingsViewModel? _settings;

    public void Dispose()
    {
        _settings?.Dispose();
        _rig.Dispose();
    }

    private static readonly ControllerPreset Diy = ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId);
    private static readonly byte[] DiyBlob = ControllerConfigCodec.Encode(ControllerPresets.ApplyTo(new ControllerConfig(), Diy));
    private static readonly IndicatorKind[] DiyKinds = { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair };

    private FakeControllerConnection Conn => _rig.Connection!;
    private ControllerViewModel Controller => _settings!.Controller;
    private HardwareViewModel Hardware => _settings!.Hardware;
    private SetupChooserViewModel Chooser => Hardware.Chooser;

    private void Settings(byte[]? mirror)
    {
        if (mirror is not null) _rig.Config.Config.ControllerConfigBlob = Convert.ToBase64String(mirror);
        _settings = _rig.CreateSettings(_ui);
        _ = _settings.Hardware;
    }

    private void Greet(ControllerConfigState state, uint crc)
    {
        Conn.SetStatus(TransportStatus.Open);
        Conn.RaiseMessage(new HelloMessage(ProtocolVersion.Current, ChipFamily.Esp32, "dev", state, crc, 5, DiyKinds));
        _ui.RunAll();
    }

    private bool AnyWrite() => Conn.SentFrames.Any(f => f.Length > 3 && f[2] == 0x88);

    [Fact]
    public void SavedCopyFirstWhenThereIsAMirror()
    {
        Settings(DiyBlob);
        Greet(ControllerConfigState.None, 0);
        Assert.Equal(BannerKind.NotSetUpHasMirror, Controller.Banner.Kind);
        Assert.True(Hardware.ShowChooser);
        Assert.Equal("How do you want to start?", Chooser.Title);
        Assert.Equal("Presets and Start empty are sent only when you Save.", Chooser.Subtitle);

        var saved = Chooser.Cards[0];
        Assert.Equal("Volco's saved copy", saved.Title);
        Assert.Equal("5 sliders, 3 buttons", saved.Summary);
        Assert.True(saved.IsPrimary);
        Assert.NotNull(saved.Picture);
        Assert.Equal(Controller.SetupPresets.Select(p => p.DisplayName),
                     Chooser.Cards.Skip(1).SkipLast(1).Select(c => c.Title));
        Assert.All(Chooser.Cards.Skip(1), c => Assert.False(c.IsPrimary));

        var empty = Chooser.Cards[^1];
        Assert.Equal("Start empty", empty.Title);
        Assert.Equal("Add sliders and buttons yourself", empty.Description);
        Assert.True(empty.IsDashed);
        Assert.Null(empty.Picture);
    }

    private void WaitForWrite()
    {
        for (var i = 0; i < 200 && !AnyWrite(); i++) { _ui.RunAll(); Thread.Sleep(10); }
    }

    [Fact]
    public void SavedCopyIsSentAtOnce()
    {
        Settings(DiyBlob);
        Greet(ControllerConfigState.None, 0);
        var saved = Chooser.Cards[0];
        Assert.True(saved.IsEnabled);
        Assert.Null(saved.ToolTip);

        saved.PickCommand.Execute(null);
        WaitForWrite();
        Assert.True(AnyWrite());                                     // the write burst carries the mirror
        Assert.True(Controller.IsSaving);
        Assert.False(Controller.HasNewDraft);                        // no draft
        Assert.False(Hardware.ShowSaveBar);                          // no save bar
    }

    [Fact]
    public void SavedCopyNeedsAConnectedController()
    {
        Settings(DiyBlob);                                           // no Hello yet
        var saved = Chooser.Cards[0];
        Assert.Equal("Volco's saved copy", saved.Title);
        Assert.False(saved.IsEnabled);
        Assert.Equal("Connect the controller first", saved.ToolTip);
        Controller.SetupApplySavedCommand.Execute(null);
        Assert.False(AnyWrite());
        Assert.False(Controller.IsSaving);

        Greet(ControllerConfigState.None, 0);                        // the Hello makes the link Ready: no manual Refresh
        Assert.True(Chooser.Cards[0].IsEnabled);
        Assert.Null(Chooser.Cards[0].ToolTip);
    }

    [Fact]
    public void NoSavedCopyWithoutAMirror()
    {
        Settings(null);
        Greet(ControllerConfigState.None, 0);
        Assert.Equal(BannerKind.NotSetUp, Controller.Banner.Kind);
        Assert.True(Hardware.ShowChooser);
        Assert.DoesNotContain(Chooser.Cards, c => c.Title == "Volco's saved copy");
        Assert.Equal("DIY reference", Chooser.Cards[0].Title);
        Assert.Equal("5 sliders, 3 buttons", Chooser.Cards[0].Summary);
        Assert.Equal("Start empty", Chooser.Cards[^1].Title);
    }

    [Fact]
    public void PresetStillStartsADraft()
    {
        Settings(DiyBlob);
        Greet(ControllerConfigState.None, 0);
        Chooser.Cards.Single(c => c.Title == "DIY reference").PickCommand.Execute(null);
        _ui.RunAll();
        Assert.False(AnyWrite());
        Assert.True(Controller.HasNewDraft);
    }

    [Fact]
    public void PickingAPresetStartsADraft()
    {
        Settings(null);
        Greet(ControllerConfigState.None, 0);
        Chooser.Cards.Single(c => c.Title == "DIY reference").PickCommand.Execute(null);

        Assert.False(Hardware.ShowChooser);
        Assert.True(Controller.CanEdit);
        Assert.Equal(5, Controller.Draft.Sliders.Count);
        Assert.Equal(5, Hardware.Sliders.Count);
        Assert.Equal("This setup isn't on the controller yet", Hardware.SaveBarText);
        Assert.False(AnyWrite());

        Controller.RevertCommand.Execute(null);                       // the draft goes: the chooser is back
        Assert.True(Hardware.ShowChooser);
    }

    [Fact]
    public void AnUnusableSetupShowsTheChooser()
    {
        Settings(null);
        Greet(ControllerConfigState.Damaged, 0x77);
        Assert.Equal(BannerKind.Unusable, Controller.Banner.Kind);
        Assert.True(Hardware.ShowChooser);

        Chooser.Cards[^1].PickCommand.Execute(null);                 // Start empty
        Assert.False(Hardware.ShowChooser);
        Assert.Empty(Controller.Draft.Sliders);
        Assert.True(Controller.CanEdit);
    }

    [Fact]
    public void StartOverShowsTheChooserUntilAPick()
    {
        Settings(DiyBlob);
        Greet(ControllerConfigState.Stored, ControllerConfigCodec.Crc32(DiyBlob));
        Assert.False(Hardware.ShowChooser);

        Hardware.StartOver();
        Assert.True(Hardware.ShowChooser);
        Assert.Equal("Volco's saved copy", Chooser.Cards[0].Title);

        Chooser.Cards[^1].PickCommand.Execute(null);
        Assert.False(Hardware.ShowChooser);
        Assert.Empty(Controller.Draft.Sliders);
        Assert.True(Hardware.ShowSaveBar);
    }
}
