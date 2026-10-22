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

/// <summary>Slider-calibration spec, "What the Hardware section shows": the slider's Auto-calibrate
/// draft field, its calibration caption from the controller's reports, and when Re-calibrate is enabled.</summary>
public sealed class SliderCalibrationTests : IDisposable
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

    private void SetUpInSync()
    {
        var c = _rig.Config.Config;
        c.Sliders = "abcde".Select((n, i) => new SliderConfig { Index = i, Name = n.ToString() }).ToList();
        c.Buttons = Enumerable.Range(0, 3).Select(i => new ButtonConfig { Index = i }).ToList();
        c.ControllerConfigBlob = Convert.ToBase64String(DiyBlob);
        _settings = _rig.CreateSettings(_ui);
        _ = _settings.Controls;
        Conn.SetStatus(TransportStatus.Open);
        Conn.RaiseMessage(new HelloMessage(ProtocolVersion.Current, ChipFamily.Esp32, "dev", ControllerConfigState.Stored,
                                           ControllerConfigCodec.Crc32(DiyBlob), 5, DiyKinds));
        _ui.RunAll();
        Assert.True(Controller.CanEdit);
    }

    private void Greet(uint crc)
    {
        Conn.RaiseMessage(new HelloMessage(ProtocolVersion.Current, ChipFamily.Esp32, "dev", ControllerConfigState.Stored, crc, 5, DiyKinds));
        _ui.RunAll();
    }

    private SliderWiringViewModel Select(int slider)
    {
        _settings!.Controls.Select(Hardware.Sliders[slider]);
        return Assert.IsType<SliderWiringViewModel>(Hardware.Inspected);
    }

    private void Report(int slider, bool learned, bool auto, ushort low = 170, ushort high = 2550, bool sweeping = false)
    {
        Conn.RaiseMessage(new CalibrationMessage(slider, learned, sweeping, auto, low, high));
        _ui.RunAll();
    }

    [Fact]
    public void AutoCalibrateEditsTheDraftAndCounts()
    {
        SetUpInSync();
        var s = Select(1);
        Assert.True(s.AutoCalibrate);
        Assert.Null(s.AutoCalibrateNote);

        s.AutoCalibrate = false;
        Assert.False(Controller.Draft.Sliders[1].AutoCalibrate);
        Assert.True(Hardware.ShowSaveBar);
        Assert.Equal("1 change not saved", Hardware.SaveBarText);
        Assert.True(Hardware.Sliders[1].Wiring!.HasChange);
        Assert.Equal("Saving forgets this slider's calibration", s.AutoCalibrateNote);

        s.AutoCalibrate = true;   // back as saved: nothing is unsaved
        Assert.Null(s.AutoCalibrateNote);
        Assert.False(Controller.IsDirty);
        Assert.False(Hardware.ShowSaveBar);
    }

    [Fact]
    public void AutoCalibrateCantChangeWhileTheDraftCantBeEdited()
    {
        SetUpInSync();
        var s = Select(0);
        Controller.StartTestCommand.Execute(null);
        s.AutoCalibrate = false;
        Assert.True(Controller.Draft.Sliders[0].AutoCalibrate);
        Assert.False(Controller.IsDirty);
    }

    [Fact]
    public void TheCaptionFollowsTheReports()
    {
        SetUpInSync();
        var s = Select(2);
        Assert.Equal("—", s.CalibrationText);   // queried after the Hello, not answered yet

        Report(2, learned: true, auto: true);
        Assert.Equal("Calibrated: 170-2550", s.CalibrationText);
        Report(2, learned: false, auto: true, low: 4095, high: 0);
        Assert.Equal("Not calibrated yet: move the slider end to end", s.CalibrationText);
        Report(2, learned: false, auto: false, low: 4095, high: 0);
        Assert.Equal("Not calibrated: uses the raw reading", s.CalibrationText);
        Report(3, learned: true, auto: true);   // another slider's
        Assert.Equal("Not calibrated: uses the raw reading", s.CalibrationText);

        Greet(ControllerConfigCodec.Crc32(DiyBlob));   // a Hello forgets the reports until the answers
        Assert.Equal("—", s.CalibrationText);
        Report(2, learned: true, auto: true);
        Assert.Equal("Calibrated: 170-2550", s.CalibrationText);

        Conn.SetStatus(TransportStatus.Closed);   // not connected: no caption
        _ui.RunAll();
        Assert.Equal("—", s.CalibrationText);
    }

    [Fact]
    public void RecalibrateNeedsASavedSliderAndNoTest()
    {
        SetUpInSync();
        var s = Select(1);
        Assert.True(s.CanRecalibrate);
        Assert.True(s.RecalibrateCommand.CanExecute(null));
        Assert.Null(s.RecalibrateTooltip);

        // An unsaved change of this slider (its pin, its Auto-calibrate): save first.
        Controller.SetSliderPin(1, 39);
        Assert.False(s.CanRecalibrate);
        Assert.False(s.RecalibrateCommand.CanExecute(null));
        Assert.Equal("Save to controller first", s.RecalibrateTooltip);
        Controller.RevertCommand.Execute(null);
        s = Select(1);
        Assert.True(s.CanRecalibrate);
        s.AutoCalibrate = false;
        Assert.False(s.CanRecalibrate);
        Assert.Equal("Save to controller first", s.RecalibrateTooltip);
        s.AutoCalibrate = true;
        Assert.True(s.CanRecalibrate);

        // Another slider's change doesn't hold it off.
        Controller.SetSliderPin(3, 39);
        Assert.True(s.CanRecalibrate);
        Controller.RevertCommand.Execute(null);
        s = Select(1);

        // A test runs.
        Controller.StartTestCommand.Execute(null);
        Assert.False(s.CanRecalibrate);
        Assert.Null(s.RecalibrateTooltip);
        Controller.StopTestCommand.Execute(null);
        Assert.True(s.CanRecalibrate);

        // A new slider has nothing to calibrate yet.
        Controller.AddSliderCommand.Execute(null);
        var added = Select(5);
        Assert.False(added.CanRecalibrate);
        Assert.Equal("—", added.CalibrationText);
        Assert.Null(added.AutoCalibrateNote);
        Controller.RevertCommand.Execute(null);

        // Not connected.
        s = Select(1);
        Conn.SetStatus(TransportStatus.Closed);
        _ui.RunAll();
        Assert.False(s.CanRecalibrate);
    }

    [Fact]
    public void RecalibrateIsOffWhileTheControllerHoldsAnotherSetup()
    {
        SetUpInSync();
        Greet(ControllerConfigCodec.Crc32(DiyBlob) ^ 1);   // the controller's setup isn't Volco's copy
        Assert.False(Controller.InSync);
        var s = Select(1);
        Assert.False(s.CanRecalibrate);
        Assert.False(s.RecalibrateCommand.CanExecute(null));
        Assert.Equal("Volco's setup differs from the controller's", s.RecalibrateTooltip);
    }

    [Fact]
    public void ReCalibrateOpensTheSweepOnTheSlidersInput()
    {
        SetUpInSync();
        var s = Select(1);
        s.RecalibrateCommand.Execute(null);
        var dialog = Assert.IsType<CalibrationDialogViewModel>(Hardware.Calibration);
        Assert.True(Hardware.IsCalibrationOpen);
        Assert.Equal("Calibrate Slider 2", dialog.Title);
        Assert.Equal(1, dialog.SliderIndex);
        Assert.True(_rig.Runtime.IsSliderHeld(1));

        Hardware.CloseCalibration();   // closed before the sweep ended: cancelled
        Assert.False(Hardware.IsCalibrationOpen);
        Assert.False(_rig.Runtime.IsSliderHeld(1));
        Assert.Contains(Conn.SentFrames, f => f.AsSpan().SequenceEqual(ProtocolEncoder.EncodeCalibrate(1, CalibrateAction.Cancel)));
    }
}
