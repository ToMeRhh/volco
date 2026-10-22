using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.ViewModels;
using Volco.App.ViewModels.Hardware;
using Volco.App.Views;
using Volco.App.Views.Stage;
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Volco.Core.Tests.Runtime;
using Xunit;
using Button = Avalonia.Controls.Button;

namespace Volco.App.Tests.Shell;

/// <summary>Slider-calibration spec, "The sweep": Re-calibrate opens a modal dialog over the
/// settings window that holds the slider away from audio while the controller sweeps; Done once
/// learned, Cancel (Escape, the scrim), and a dropped link close it.</summary>
public sealed class CalibrationDialogTests : IDisposable
{
    public CalibrationDialogTests() => StageTransitions.Enabled = false;

    public void Dispose() => StageTransitions.Enabled = false;

    private static readonly ControllerPreset Diy = ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId);
    private static readonly byte[] DiyBlob = ControllerConfigCodec.Encode(ControllerPresets.ApplyTo(new ControllerConfig(), Diy));
    private static readonly IndicatorKind[] DiyKinds = { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair };

    private sealed record Shown(SettingsWindow Window, TestRig Rig, SettingsViewModel Vm) : IDisposable
    {
        public FakeControllerConnection Conn => Rig.Connection!;

        public void Dispose()
        {
            Window.AllowClose = true;
            Window.Close();
            Vm.Dispose();
            Rig.Dispose();
        }
    }

    /// <summary>The settings window on the Controller page, in sync with a DIY controller, slider 1
    /// (Slider 1 on the default output) selected.</summary>
    private static Shown Show()
    {
        var rig = new TestRig();
        var speakers = new AudioDevice("spk", "Speakers", AudioFlow.Render, true);
        rig.Engine.Devices = new List<AudioDevice> { speakers };
        rig.Engine.DefaultRender = speakers;
        rig.Engine.DeviceStates["spk"] = (0.5f, false);
        var c = rig.Config.Config;
        c.Sliders = Enumerable.Range(0, 5).Select(i => new SliderConfig { Index = i, Type = SliderMappingType.Device }).ToList();
        c.Buttons = Enumerable.Range(0, 3).Select(i => new ButtonConfig { Index = i }).ToList();
        c.ControllerConfigBlob = Convert.ToBase64String(DiyBlob);
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var window = new SettingsWindow(vm, rig.Runtime) { Width = 1100, Height = 720 };
        window.Show();
        window.ShowPage("controller");
        rig.Connection!.SetStatus(TransportStatus.Open);
        rig.Connection.RaiseMessage(new HelloMessage(ProtocolVersion.Current, ChipFamily.Esp32, "dev", ControllerConfigState.Stored,
                                                     ControllerConfigCodec.Crc32(DiyBlob), 5, DiyKinds));
        Until(window, () => vm.Controller.CanEdit);
        vm.Controls.Select(vm.Hardware.Sliders[0]);
        Until(window, () => vm.Hardware.Inspected is SliderWiringViewModel { CanRecalibrate: true });
        return new Shown(window, rig, vm);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Until(Window window, Func<bool> condition, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            Settle(window);
            if (condition()) return;
            if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("condition not met");
            Thread.Sleep(5);
        }
    }

    private static T In<T>(Visual root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static bool Sent(Shown s, CalibrateAction action, int slider = 0)
        => s.Conn.SentFrames.Any(f => f.AsSpan().SequenceEqual(ProtocolEncoder.EncodeCalibrate(slider, action)));

    private static void Report(Shown s, bool sweeping, bool learned, ushort low = 170, ushort high = 2550)
    {
        s.Conn.RaiseMessage(new CalibrationMessage(0, learned, sweeping, autoCalibrate: true, low, high));
        Settle(s.Window);
    }

    private static int VolumeCalls(Shown s) => s.Rig.Engine.Snapshot(s.Rig.Engine.DeviceVolumeCalls).Count;

    /// <summary>Clicks Re-calibrate in Slider 1's Hardware section; the dialog is open.</summary>
    private static Border Recalibrate(Shown s)
    {
        var button = In<Button>(s.Window, "RecalibrateButton");
        Assert.True(button.IsEffectivelyEnabled);
        button.Command!.Execute(null);
        Settle(s.Window);
        var overlay = In<Border>(s.Window, "CalibrationOverlay");
        Assert.True(overlay.IsVisible);
        return overlay;
    }

    [AvaloniaFact]
    public void TheRowsShowInTheSlidersHardwareSection()
    {
        using var s = Show();
        Assert.Equal("Auto-calibrate", In<TextBlock>(s.Window, "AutoCalibrateLabel").Text);
        Assert.True(In<ToggleSwitch>(s.Window, "AutoCalibrateToggle").IsChecked);
        Assert.Equal("—", In<TextBlock>(s.Window, "CalibrationText").Text);
        Report(s, sweeping: false, learned: true);
        Until(s.Window, () => In<TextBlock>(s.Window, "CalibrationText").Text == "Calibrated: 170-2550");

        In<ToggleSwitch>(s.Window, "AutoCalibrateToggle").IsChecked = false;
        Settle(s.Window);
        Assert.Equal("Saving forgets this slider's calibration", In<TextBlock>(s.Window, "AutoCalibrateNote").Text);
        Assert.True(In<TextBlock>(s.Window, "AutoCalibrateNote").IsVisible);
        Assert.False(In<Button>(s.Window, "RecalibrateButton").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void RecalibrateStartsASweepAndHoldsTheSlider()
    {
        using var s = Show();
        var overlay = Recalibrate(s);
        Assert.Equal("Calibrate Slider 1", In<TextBlock>(overlay, "CalibrationTitle").Text);
        Assert.True(Sent(s, CalibrateAction.Start));
        Assert.True(s.Rig.Runtime.IsSliderHeld(0));
        // The focus moved into the dialog.
        var card = In<Border>(s.Window, "CalibrationCard");
        var focused = s.Window.FocusManager?.GetFocusedElement() as Visual;
        Assert.True(focused is not null && (ReferenceEquals(focused, card) || card.IsVisualAncestorOf(focused)), $"focus on {focused}");

        // Moving the slider end to end doesn't reach audio.
        var before = VolumeCalls(s);
        s.Conn.RaiseMessage(new SliderMessage(0, 4095));
        s.Conn.RaiseMessage(new SliderMessage(0, 0));
        Thread.Sleep(150);
        Settle(s.Window);
        Assert.Equal(before, VolumeCalls(s));
    }

    [AvaloniaFact]
    public void DoneEnablesOnceLearned()
    {
        using var s = Show();
        var overlay = Recalibrate(s);
        var done = In<Button>(overlay, "CalibrationDoneButton");
        Assert.False(done.IsEffectivelyEnabled);
        Assert.True(In<TextBlock>(overlay, "CalibrationKeepMoving").IsVisible);
        Assert.Equal("Keep moving: the travel is too short so far", In<TextBlock>(overlay, "CalibrationKeepMoving").Text);

        Report(s, sweeping: true, learned: false, low: 4095, high: 0);   // the Start's answer: the ends forgotten
        Assert.Equal("—", In<TextBlock>(overlay, "CalibrationLow").Text);
        Report(s, sweeping: true, learned: false, low: 1000, high: 1500);
        Assert.Equal("1000", In<TextBlock>(overlay, "CalibrationLow").Text);
        Assert.Equal("1500", In<TextBlock>(overlay, "CalibrationHigh").Text);
        Assert.False(done.IsEffectivelyEnabled);

        Report(s, sweeping: true, learned: true, low: 170, high: 2550);
        Assert.True(done.IsEffectivelyEnabled);
        Assert.False(In<TextBlock>(overlay, "CalibrationKeepMoving").IsVisible);
        var fill = In<Border>(overlay, "CalibrationFill");
        Assert.True(fill.Width > 0, $"fill {fill.Width}");
    }

    [AvaloniaFact]
    public void DoneSendsDoneAndClosesOnTheEndingReport()
    {
        using var s = Show();
        var overlay = Recalibrate(s);
        Report(s, sweeping: true, learned: true);
        In<Button>(overlay, "CalibrationDoneButton").Command!.Execute(null);
        Settle(s.Window);
        Assert.True(Sent(s, CalibrateAction.Done));
        Assert.True(overlay.IsVisible);   // until the controller says the sweep ended

        Report(s, sweeping: false, learned: true);
        Until(s.Window, () => !overlay.IsVisible);
        Assert.False(s.Rig.Runtime.IsSliderHeld(0));
        Assert.False(Sent(s, CalibrateAction.Cancel));
        Until(s.Window, () => In<TextBlock>(s.Window, "CalibrationText").Text == "Calibrated: 170-2550");

        var before = VolumeCalls(s);
        s.Conn.RaiseMessage(new SliderMessage(0, 4095));   // the fresh position reaches audio
        Until(s.Window, () => VolumeCalls(s) > before);
    }

    [AvaloniaFact]
    public void EscapeCancels()
    {
        using var s = Show();
        var overlay = Recalibrate(s);
        Report(s, sweeping: true, learned: false, low: 1000, high: 1500);
        s.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Settle(s.Window);
        Assert.True(Sent(s, CalibrateAction.Cancel));
        Report(s, sweeping: false, learned: true);
        Until(s.Window, () => !overlay.IsVisible);
        Assert.False(s.Rig.Runtime.IsSliderHeld(0));
        Assert.Null(s.Vm.Hardware.Calibration);
    }

    [AvaloniaFact]
    public void CancelClosesWithoutAnAnswer()
    {
        using var s = Show();
        var overlay = Recalibrate(s);
        In<Button>(overlay, "CalibrationCancelButton").Command!.Execute(null);
        Settle(s.Window);
        Assert.True(Sent(s, CalibrateAction.Cancel));
        Until(s.Window, () => !overlay.IsVisible, ms: 5000);   // after the 2 s wait
        Assert.False(s.Rig.Runtime.IsSliderHeld(0));
    }

    [AvaloniaFact]
    public void ALostLinkClosesTheDialog()
    {
        using var s = Show();
        var overlay = Recalibrate(s);
        Report(s, sweeping: true, learned: false);
        s.Conn.SetStatus(TransportStatus.Closed);
        Until(s.Window, () => !overlay.IsVisible);
        Assert.False(s.Rig.Runtime.IsSliderHeld(0));
        Assert.Null(s.Vm.Hardware.Calibration);
    }

    [AvaloniaFact]
    public void HidingTheWindowCancels()
    {
        using var s = Show();
        var overlay = Recalibrate(s);
        s.Window.Close();   // hides
        Settle(s.Window);
        Assert.Null(s.Vm.Hardware.Calibration);
        Assert.False(overlay.IsVisible);
        Assert.True(Sent(s, CalibrateAction.Cancel));
        Assert.False(s.Rig.Runtime.IsSliderHeld(0));
    }
}
