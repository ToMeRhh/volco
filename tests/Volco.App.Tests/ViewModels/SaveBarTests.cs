using Volco.App.ViewModels;
using Volco.App.ViewModels.Hardware;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Volco.Core.Runtime;
using Volco.Core.Tests.Runtime;
using Xunit;

namespace Volco.App.Tests.ViewModels;

/// <summary>Hardware-page spec §6.5: the save bar counts what is unsaved, says so for a new
/// draft, can't save a draft with problems, and Discard restores everything.</summary>
public sealed class SaveBarTests : IDisposable
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
    private AppConfig Config => _rig.Config.Config;
    private ControllerViewModel Controller => _settings!.Controller;
    private HardwareViewModel Hardware => _settings!.Hardware;

    private void Settings(byte[]? mirror)
    {
        Config.Sliders = "abcde".Select((c, i) => new SliderConfig { Index = i, Name = c.ToString() }).ToList();
        Config.Buttons = Enumerable.Range(0, 3).Select(i => new ButtonConfig { Index = i }).ToList();
        if (mirror is not null) Config.ControllerConfigBlob = Convert.ToBase64String(mirror);
        _settings = _rig.CreateSettings(_ui);
        _ = _settings.Controls;
    }

    private static HelloMessage Hello(ControllerConfigState state, uint crc)
        => new(ProtocolVersion.Current, ChipFamily.Esp32, "dev", state, crc, 5, DiyKinds);

    private void SetUpInSync()
    {
        Settings(DiyBlob);
        Conn.SetStatus(TransportStatus.Open);
        Conn.RaiseMessage(Hello(ControllerConfigState.Stored, ControllerConfigCodec.Crc32(DiyBlob)));
        _ui.RunAll();
        Assert.True(Controller.CanEdit);
    }

    [Fact]
    public void NothingChanged_NoBar()
    {
        SetUpInSync();
        Assert.False(Hardware.ShowSaveBar);
        Assert.Null(Hardware.SaveBarNote);
    }

    [Fact]
    public void AControllerChange_ShowsTheRestartNote()
    {
        SetUpInSync();
        Controller.SetSliderPin(1, 39);
        Assert.Equal("Saving restarts the controller", Hardware.SaveBarNote);
    }

    [Fact]
    public void CountsChangedThings()
    {
        SetUpInSync();
        Controller.SetSliderPin(1, 39);
        Assert.True(Hardware.ShowSaveBar);
        Assert.Equal("1 change not saved", Hardware.SaveBarText);
        Controller.SetSliderPin(2, 36);
        Controller.Brightness = 100;
        Assert.Equal("3 changes not saved", Hardware.SaveBarText);
    }

    [Fact]
    public void RemovalsAndAdditionsCountOnceEach()
    {
        SetUpInSync();
        Controller.RemoveSlider(2);
        Assert.Equal("1 change not saved", Hardware.SaveBarText);
        Controller.AddSliderCommand.Execute(null);
        Assert.Equal("2 changes not saved", Hardware.SaveBarText);
    }

    [Fact]
    public void SettingUpSaysNotOnTheControllerYet()
    {
        Settings(null);
        Conn.SetStatus(TransportStatus.Open);
        Conn.RaiseMessage(Hello(ControllerConfigState.None, 0));
        _ui.RunAll();
        Controller.StartFromPresetCommand.Execute(Diy);
        Assert.True(Hardware.ShowSaveBar);
        Assert.Equal("This setup isn't on the controller yet", Hardware.SaveBarText);
    }

    [Fact]
    public void SaveDisabledWithProblems()
    {
        SetUpInSync();
        Controller.SetSliderPin(1, 39);
        Assert.True(Hardware.SaveCommand.CanExecute(null));
        Controller.SetSliderPin(1, 33);   // Slider 1's pin
        Assert.False(Hardware.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void DiscardAfterAMiddleRemovalRestoresEverything()
    {
        SetUpInSync();
        Controller.RemoveSlider(2);
        Controller.SetSliderPin(0, 39);
        Hardware.DiscardCommand.Execute(null);
        Assert.False(Hardware.ShowSaveBar);
        Assert.Equal(5, Hardware.Sliders.Count);
        Assert.Equal(_settings!.Controls.Sliders.Items, Hardware.Sliders);
        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, Config.Sliders.Select(s => s.Name));
        Assert.True(Controller.Layout.IsIdentity);
        Assert.False(Controller.IsDirty);
    }

    // ── Button timing (slider-feel spec, "The save rule") ────────────────────────────────────

    [Fact]
    public void TimingOnly_ShowsBar_WithoutRestartNote()
    {
        SetUpInSync();
        Controller.HoldMs = 700;
        Assert.True(Hardware.ShowSaveBar);
        Assert.Equal("1 change not saved", Hardware.SaveBarText);
        Assert.True(string.IsNullOrEmpty(Hardware.SaveBarNote));
    }

    [Fact]
    public void TimingOnly_Save_WritesConfig_SendsNothing()
    {
        SetUpInSync();
        var writes = Conn.SentFrames.Count(f => f.Length > 3 && f[2] == 0x88);
        Controller.HoldMs = 700;
        Controller.DoublePressMs = 250;
        Assert.True(Hardware.SaveCommand.CanExecute(null));
        Hardware.SaveCommand.Execute(null);
        _ui.RunAll();
        Assert.Equal(700, Config.ButtonTiming.LongPressMs);
        Assert.Equal(250, Config.ButtonTiming.DoublePressWindowMs);
        Assert.Equal(writes, Conn.SentFrames.Count(f => f.Length > 3 && f[2] == 0x88));
        Assert.False(Controller.IsSaving);
        Assert.False(Controller.IsDirty);
        Assert.False(Hardware.ShowSaveBar);
    }

    [Fact]
    public void TimingAndController_ShowsTheRestartNote()
    {
        SetUpInSync();
        Controller.HoldMs = 700;
        Controller.DebounceMs = 45;
        Assert.Equal("Saving restarts the controller", Hardware.SaveBarNote);
    }

    [Fact]
    public void CountChanges_ButtonsCardOnce()
    {
        SetUpInSync();
        Controller.HoldMs = 700;
        Controller.DoublePressMs = 250;
        Controller.DebounceMs = 45;
        Assert.Equal("1 change not saved", Hardware.SaveBarText);
        Controller.SmoothingPct = 10;
        Assert.Equal("2 changes not saved", Hardware.SaveBarText);
    }
}
