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
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Volco.Core.Tests.Runtime;
using Xunit;
using Button = Avalonia.Controls.Button;

namespace Volco.App.Tests.Shell;

/// <summary>The I2C buses dialog (controller-page spec §4.3): Controller settings › Screens has one
/// row with Edit, which opens it over the window; Escape and the scrim close it.</summary>
public sealed class I2cBusesDialogTests : IDisposable
{
    public I2cBusesDialogTests() => StageTransitions.Enabled = false;

    public void Dispose() => StageTransitions.Enabled = false;

    private static readonly ControllerPreset Diy = ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId);
    private static readonly byte[] DiyBlob = ControllerConfigCodec.Encode(ControllerPresets.ApplyTo(new ControllerConfig(), Diy));
    private static readonly IndicatorKind[] DiyKinds = { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair };

    private sealed record Shown(SettingsWindow Window, TestRig Rig, SettingsViewModel Vm) : IDisposable
    {
        public void Dispose()
        {
            Window.AllowClose = true;
            Window.Close();
            Vm.Dispose();
            Rig.Dispose();
        }
    }

    /// <summary>The settings window on the Controller page, in sync with a DIY controller whose first
    /// button has a screen (on bus 0), Controller settings open.</summary>
    private static Shown Show()
    {
        var rig = new TestRig();
        var c = rig.Config.Config;
        c.Sliders = Enumerable.Range(0, 5).Select(i => new SliderConfig { Index = i }).ToList();
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
        vm.Controller.Draft.Buttons[0].Screen = new Screen();
        vm.Controller.OnDraftEdited();
        vm.Hardware.SelectControllerSettings();
        Settle(window);
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

    private static void Click(Window window, Control control)
    {
        control.BringIntoView();
        Settle(window);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }

    [AvaloniaFact]
    public void EditOpensTheDialogAndEscapeClosesIt()
    {
        using var s = Show();
        // Controller settings has one row for the buses, no bus fields of its own
        Assert.Equal("Buses 0 and 1", In<TextBlock>(s.Window, "BusesSummary").Text);   // the DIY preset's
        Assert.DoesNotContain(s.Window.GetVisualDescendants(), v => v is Control { Name: "SdaField" });

        Click(s.Window, In<Button>(s.Window, "BusesButton"));
        var overlay = In<Border>(s.Window, "BusesOverlay");
        Assert.True(overlay.IsVisible);
        var card = In<Border>(overlay, "BusCard");
        Assert.Equal("Bus 0", In<TextBlock>(card, "BusTitle").Text);
        Assert.Equal("Used by Button 1", In<TextBlock>(card, "BusUsedBy").Text);
        Assert.True(In<Control>(card, "SdaField").IsEffectivelyVisible);
        Assert.True(In<Control>(card, "SclField").IsEffectivelyVisible);
        Assert.False(In<Button>(card, "BusRemoveButton").IsEffectivelyEnabled);   // a screen uses it
        Assert.Equal("Add bus 2", In<Button>(overlay, "AddBusButton").Content);
        // The focus is in the dialog
        var busesCard = In<Border>(s.Window, "BusesCard");
        var focused = s.Window.FocusManager?.GetFocusedElement() as Visual;
        Assert.True(ReferenceEquals(focused, busesCard) || focused is not null && busesCard.IsVisualAncestorOf(focused));

        Click(s.Window, In<Button>(overlay, "AddBusButton"));
        Assert.Equal(3, overlay.GetVisualDescendants().OfType<Border>().Count(b => b.Name == "BusCard"));
        Assert.Equal("Buses 0, 1 and 2", In<TextBlock>(s.Window, "BusesSummary").Text);

        s.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Settle(s.Window);
        Assert.False(overlay.IsVisible);
        Assert.Null(s.Vm.Hardware.Buses);
        Assert.NotNull(ControllerDraft.Bus(s.Vm.Controller.Draft, 2));   // the edit stays in the draft
    }
}
