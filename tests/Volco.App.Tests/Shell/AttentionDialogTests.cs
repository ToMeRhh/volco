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
using Volco.App.Views.Hardware;
using Volco.App.Views.Stage;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Firmware;
using Volco.Core.Runtime;
using Volco.Core.Tests.Firmware;
using Volco.Core.Tests.Runtime;
using Xunit;
using Button = Avalonia.Controls.Button;

namespace Volco.App.Tests.Shell;

/// <summary>
/// Controller-page spec §5 (controller, 2026-10-22): "No controller answers" and "Firmware too
/// old for this Volco" open a modal attention dialog once each time the state begins: the status as
/// its title, its detail, its button and Not now. Not now (Escape, the scrim) leaves the banner;
/// the button's operation takes over in the progress dialog.
/// </summary>
public sealed class AttentionDialogTests : IDisposable
{
    public AttentionDialogTests() => StageTransitions.Enabled = false;

    public void Dispose() => StageTransitions.Enabled = false;

    private static readonly FirmwareCatalog Catalog = FirmwareCatalog.Load(MemoryFirmwareSource.Valid());

    private sealed record Shown(SettingsWindow Window, TestRig Rig, SettingsViewModel Vm, FakeFlasher Flasher) : IDisposable
    {
        public ControllerStatusViewModel Status => Vm.Hardware.Status;
        public Border Overlay => Window.FindControl<Border>("AttentionOverlay")!;
        public Border Progress => Window.FindControl<Border>("ProgressOverlay")!;
        public StatusBanner Banner => Window.FindControl<StatusBanner>("StatusBanner")!;
        public string Title => In<TextBlock>(Overlay, "AttentionTitle").Text!;
        public string Detail => In<TextBlock>(Overlay, "AttentionDetail").Text!;
        public Button ActionButton => In<Button>(Overlay, "AttentionActionButton");
        public Button NotNow => In<Button>(Overlay, "AttentionNotNowButton");

        public void Dispose()
        {
            Window.AllowClose = true;
            Window.Close();
            Vm.Dispose();
            Rig.Dispose();
        }
    }

    /// <summary>The settings window on General with Volco's firmware bundled, its USB port COM3
    /// open; nothing answers on it (no controller answers after 100 ms) unless the test greets.</summary>
    private static Shown Show(bool show = true)
    {
        var rig = new TestRig(options: TestRig.NoGrace with { Firmware = Catalog, SilentAfter = TimeSpan.FromMilliseconds(100) });
        rig.Config.Config.Connection.Serial.ComPort = "COM3";
        var flasher = new FakeFlasher(() => rig.Runtime.IsSuspended);
        var service = new FirmwareService(rig.Runtime, flasher, Catalog);
        var vm = new SettingsViewModel(rig.Config, rig.Engine, rig.Runtime, new AvaloniaUiDispatcher(), service);
        var window = new SettingsWindow(vm, rig.Runtime) { Width = 1100, Height = 720 };
        if (show) window.Show();
        return new Shown(window, rig, vm, flasher);
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

    /// <summary>Lets <paramref name="ms"/> pass, settling the window.</summary>
    private static void Wait(Window window, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Settle(window);
            Thread.Sleep(5);
        }
    }

    private static T In<T>(Visual root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static void Click(Shown s, Control control)
    {
        Settle(s.Window);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), s.Window)!.Value;
        s.Window.MouseDown(point, MouseButton.Left);
        s.Window.MouseUp(point, MouseButton.Left);
        Settle(s.Window);
    }

    private static void Escape(Shown s)
    {
        s.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Settle(s.Window);
    }

    private static void NoAnswer(Shown s)
    {
        s.Rig.Connection!.SetStatus(TransportStatus.Open);
        Until(s.Window, () => s.Rig.Runtime.LinkStatus.State == LinkState.NoAnswer && s.Vm.Controller.CanUseFirmware);
    }

    [AvaloniaFact]
    public void NoAnswerOpensTheDialogOnce()
    {
        using var s = Show();
        NoAnswer(s);
        Until(s.Window, () => s.Overlay.IsEffectivelyVisible);
        Assert.Equal("No controller answers on COM3", s.Title);
        Assert.Equal("It may not have Volco's firmware yet.", s.Detail);
        Assert.Equal("Check firmware", s.ActionButton.Content);
        Assert.True(s.ActionButton.IsEffectivelyVisible);
        Assert.Equal("Not now", s.NotNow.Content);
        Assert.True(s.Overlay.ZIndex < s.Progress.ZIndex);   // the progress dialog is above it
        Assert.True(s.Overlay.IsVisualAncestorOf((Visual)s.Window.FocusManager!.GetFocusedElement()!));   // the focus is in it
        Assert.True(s.Banner.IsVisible);                      // the banner is there under it
    }

    [AvaloniaFact]
    public void NotNowLeavesTheBannerAndDoesNotReopen()
    {
        using var s = Show();
        NoAnswer(s);
        Until(s.Window, () => s.Overlay.IsVisible);

        Click(s, s.NotNow);

        Assert.False(s.Overlay.IsVisible);
        Assert.True(s.Banner.IsEffectivelyVisible);
        Assert.Equal(LinkState.NoAnswer, s.Rig.Runtime.LinkStatus.State);
        Wait(s.Window, 300);   // the state persists: it stays closed
        Assert.False(s.Overlay.IsVisible);
        Assert.True(s.Banner.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void EscapeAndTheScrimAreNotNow()
    {
        using var s = Show();
        NoAnswer(s);
        Until(s.Window, () => s.Overlay.IsVisible);
        Escape(s);
        Assert.False(s.Overlay.IsVisible);
        Assert.False(s.Status.AttentionPending);
        Assert.True(s.Banner.IsEffectivelyVisible);

        // Again after it clears (unplugged) and returns, and the scrim closes it too.
        s.Rig.Connection!.SetStatus(TransportStatus.Closed);
        Until(s.Window, () => s.Rig.Runtime.LinkStatus.State == LinkState.Searching);
        NoAnswer(s);
        Until(s.Window, () => s.Overlay.IsVisible);
        s.Window.MouseDown(new Point(4, 300), MouseButton.Left);   // over the sidebar: the scrim
        s.Window.MouseUp(new Point(4, 300), MouseButton.Left);
        Settle(s.Window);
        Assert.False(s.Overlay.IsVisible);
        Assert.True(s.Banner.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void CheckFirmwareFromTheDialogStartsTheProbe()
    {
        using var s = Show();
        NoAnswer(s);
        Until(s.Window, () => s.Overlay.IsVisible && s.ActionButton.IsEffectivelyEnabled);

        Click(s, s.ActionButton);

        Until(s.Window, () => s.Status.Progress.IsOpen);
        Assert.False(s.Overlay.IsVisible);                 // the progress dialog takes over
        Assert.True(s.Progress.IsEffectivelyVisible);
        Assert.Equal("Checking firmware", s.Status.Progress.Title);
        Until(s.Window, () => s.Status.Progress.IsDone);
        Assert.Equal("probe COM3", s.Flasher.Calls.Single());

        // The check's reset and reconnect, with the board still silent, don't reopen it.
        s.Status.Progress.Close();
        Settle(s.Window);
        Until(s.Window, () => s.Rig.Runtime.LinkStatus.State == LinkState.NoAnswer || s.Rig.Runtime.LinkStatus.State == LinkState.Waiting);
        Wait(s.Window, 300);
        Assert.False(s.Overlay.IsVisible);
    }

    [AvaloniaFact]
    public void ClearingAndReenteringNoAnswerReopensIt()
    {
        using var s = Show();
        NoAnswer(s);
        Until(s.Window, () => s.Overlay.IsVisible);
        Click(s, s.NotNow);
        Assert.False(s.Overlay.IsVisible);

        // A controller answers: the state clears.
        s.Rig.Connection!.RaiseMessage(new HelloMessage(ProtocolVersion.Current, 5, new[] { IndicatorKind.Led }));
        Until(s.Window, () => s.Rig.Runtime.LinkStatus.IsConnected);
        Assert.False(s.Overlay.IsVisible);

        // Unplugged and back with nothing answering: it begins again.
        s.Rig.Connection.SetStatus(TransportStatus.Closed);
        Until(s.Window, () => s.Rig.Runtime.LinkStatus.State == LinkState.Searching);
        Assert.False(s.Overlay.IsVisible);
        NoAnswer(s);
        Until(s.Window, () => s.Overlay.IsVisible);
        Assert.Equal("No controller answers on COM3", s.Title);
    }

    [AvaloniaFact]
    public void IncompatibleOpensItWithUpdate()
    {
        using var s = Show();
        s.Rig.Connection!.SetStatus(TransportStatus.Open);
        s.Rig.Connection.RaiseMessage(HelloMessage.VersionOnly(9));
        Until(s.Window, () => s.Rig.Runtime.LinkStatus.State == LinkState.Incompatible && s.Overlay.IsVisible
                              && (string?)s.ActionButton.Content == "Update");
        Assert.Equal("Firmware too old for this Volco", s.Title);
        Assert.Equal("Update it to use the controller again.", s.Detail);
        Until(s.Window, () => s.ActionButton.IsEffectivelyEnabled);

        Click(s, s.ActionButton);

        Until(s.Window, () => s.Status.Progress.IsOpen);
        Assert.False(s.Overlay.IsVisible);
        Assert.Equal("Updating firmware", s.Status.Progress.Title);
        Until(s.Window, () => s.Flasher.Calls.Count == 1);
        Assert.Equal("flash COM3 Update", s.Flasher.Calls.Single());
    }

    /// <summary>A state that begins while the window is hidden shows its dialog when the window opens.</summary>
    [AvaloniaFact]
    public void AStateThatBeganWhileHiddenShowsOnOpening()
    {
        using var s = Show(show: false);
        NoAnswer(s);
        Until(s.Window, () => s.Status.AttentionPending);
        Assert.False(s.Overlay.IsEffectivelyVisible);   // nothing is shown while the window is hidden

        s.Window.Show();
        Until(s.Window, () => s.Overlay.IsEffectivelyVisible);
        Assert.Equal("No controller answers on COM3", s.Title);
    }
}
