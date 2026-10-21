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
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Volco.Core.Firmware;
using Volco.Core.Runtime;
using Volco.Core.Tests.Firmware;
using Volco.Core.Tests.Runtime;
using Xunit;
using Button = Avalonia.Controls.Button;

namespace Volco.App.Tests.Shell;

/// <summary>
/// Controller-page spec §5, "Running, don't interrupt": a firmware install, update or check, and a
/// save's sending and restart, run in a modal progress dialog over the window. Nothing closes it
/// while it writes; at the end it shows the result and its next step, and closing it leaves what
/// remains to the banner.
/// </summary>
public sealed class ProgressDialogTests : IDisposable
{
    public ProgressDialogTests() => StageTransitions.Enabled = false;

    public void Dispose()
    {
        StageTransitions.Enabled = false;
        ProgressDialogViewModel.SavedHold = TimeSpan.Zero;
    }

    private static readonly FirmwareCatalog Catalog = FirmwareCatalog.Load(MemoryFirmwareSource.Valid());
    private static readonly ControllerPreset Diy = ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId);
    private static readonly byte[] DiyBlob = ControllerConfigCodec.Encode(ControllerPresets.ApplyTo(new ControllerConfig(), Diy));
    private static readonly IndicatorKind[] DiyKinds = { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair };

    private sealed record Shown(SettingsWindow Window, TestRig Rig, SettingsViewModel Vm, FakeFlasher Flasher) : IDisposable
    {
        public ProgressDialogViewModel Progress => Vm.Hardware.Status.Progress;
        public Border Overlay => Window.FindControl<Border>("ProgressOverlay")!;
        public StatusBanner Banner => Window.FindControl<StatusBanner>("StatusBanner")!;
        public string NavTag => ((NavItem)Window.FindControl<ListBox>("NavList")!.SelectedItem!).Tag;

        public void Dispose()
        {
            Window.AllowClose = true;
            Window.Close();
            Vm.Dispose();
            Rig.Dispose();
        }
    }

    /// <summary>The settings window on General over Volco's DIY copy on COM3, with Volco's firmware
    /// bundled, greeted by a controller running <paramref name="firmwareId"/> that holds the copy.</summary>
    private static Shown Show(string firmwareId = "dev")
    {
        var rig = new TestRig(options: TestRig.NoGrace with { Firmware = Catalog, SilentAfter = TimeSpan.FromSeconds(5) });
        var c = rig.Config.Config;
        c.Connection.Serial.ComPort = "COM3";
        c.Sliders = Enumerable.Range(0, 5).Select(i => new SliderConfig { Index = i, Type = SliderMappingType.None }).ToList();
        c.Buttons = Enumerable.Range(0, 3).Select(i => new ButtonConfig { Index = i }).ToList();
        c.ControllerConfigBlob = Convert.ToBase64String(DiyBlob);
        var flasher = new FakeFlasher(() => rig.Runtime.IsSuspended);
        var service = new FirmwareService(rig.Runtime, flasher, Catalog);
        var vm = new SettingsViewModel(rig.Config, rig.Engine, rig.Runtime, new AvaloniaUiDispatcher(), service);
        var window = new SettingsWindow(vm, rig.Runtime) { Width = 1100, Height = 720 };
        window.Show();
        rig.Connection!.SetStatus(TransportStatus.Open);
        rig.Connection.RaiseMessage(Hello(firmwareId, ControllerConfigState.Stored, ControllerConfigCodec.Crc32(DiyBlob)));
        Until(window, () => vm.Controller.InSync && vm.Controller.CanUseFirmware);
        return new Shown(window, rig, vm, flasher);
    }

    private static HelloMessage Hello(string firmwareId, ControllerConfigState state, uint crc)
        => new(ProtocolVersion.Current, ChipFamily.Esp32, firmwareId, state, crc, 5, DiyKinds);

    /// <summary>After the write the connection comes back (a new one): the controller boots Volco's
    /// firmware and says Hello with <paramref name="state"/> and <paramref name="crc"/>.</summary>
    private static void Reboot(Shown s, FakeControllerConnection before, ControllerConfigState state, uint crc)
    {
        Until(s.Window, () => !ReferenceEquals(s.Rig.Connection, before) && !s.Rig.Runtime.IsSuspended
                              && s.Rig.Connection!.ConnectCount == 1);
        s.Rig.Connection!.SetStatus(TransportStatus.Open);
        s.Rig.Connection.RaiseMessage(Hello(Catalog.BundledVersion!, state, crc));
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

    private static void Click(Shown s, Control control)
    {
        Settle(s.Window);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), s.Window)!.Value;
        s.Window.MouseDown(point, MouseButton.Left);
        s.Window.MouseUp(point, MouseButton.Left);
        Settle(s.Window);
    }

    private static void ClickScrim(Shown s)
    {
        s.Window.MouseDown(new Point(4, 300), MouseButton.Left);   // over the sidebar
        s.Window.MouseUp(new Point(4, 300), MouseButton.Left);
        Settle(s.Window);
    }

    private static void Escape(Shown s)
    {
        s.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Settle(s.Window);
    }

    private static void Install(Shown s)
    {
        s.Vm.Controller.InstallFirmwareCommand.Execute(null);   // the Reinstall confirmation
        Settle(s.Window);
        Click(s, In<Button>(s.Window, "ConfirmReinstallButton"));
    }

    // ── While it runs ────────────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void InstallShowsTheProgressDialogAndBlocks()
    {
        using var s = Show();
        var release = new TaskCompletionSource();
        s.Flasher.DuringFlash = () => release.Task;
        var before = s.Rig.Connection!;
        Install(s);

        Until(s.Window, () => s.Progress is { IsOpen: true, IsRunning: true, Progress: 0.5 });
        Assert.True(s.Overlay.IsEffectivelyVisible);
        Assert.True(s.Overlay.ZIndex > s.Window.FindControl<Border>("LeaveOverlay")!.ZIndex);
        Assert.Equal("Installing firmware", In<TextBlock>(s.Overlay, "ProgressTitle").Text);
        Assert.Equal("Writing the firmware… 50%", In<TextBlock>(s.Overlay, "ProgressStep").Text);
        Assert.Equal(0.5, In<ProgressBar>(s.Overlay, "ProgressBar").Value);
        var warning = In<TextBlock>(s.Overlay, "ProgressWarning");
        Assert.True(warning.IsEffectivelyVisible);
        Assert.Equal("Don't unplug the controller.", warning.Text);
        Assert.False(In<Button>(s.Overlay, "ProgressCloseButton").IsEffectivelyEnabled);
        Assert.False(s.Banner.IsVisible);   // the dialog shows the running operation, not the banner
        Assert.Equal(StatusLevel.Running, s.Vm.Hardware.Status.Level);

        // Nothing closes it while it writes, and nothing under it takes a key or a click.
        Escape(s);
        Assert.True(s.Progress.IsOpen);
        ClickScrim(s);
        Assert.True(s.Progress.IsOpen);
        s.Progress.CloseCommand.Execute(null);
        Assert.True(s.Progress.IsOpen);
        var nav = s.Window.FindControl<ListBox>("NavList")!;
        nav.Focus();
        s.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Settle(s.Window);
        Assert.Equal("general", s.NavTag);

        release.SetResult();
        Reboot(s, before, ControllerConfigState.None, 0);
        Until(s.Window, () => s.Progress.IsDone);
        Assert.True(s.Overlay.IsVisible);   // the result stays until Close
        Assert.True(In<Button>(s.Overlay, "ProgressCloseButton").IsEffectivelyEnabled);
        Assert.False(In<TextBlock>(s.Overlay, "ProgressWarning").IsEffectivelyVisible);
    }

    // ── At the end ───────────────────────────────────────────────────────────────────────────

    /// <summary>An update keeps the setup: its next step is the test, on the Controller page.</summary>
    [AvaloniaFact]
    public void AFinishedUpdateOffersTheTest()
    {
        using var s = Show(firmwareId: "0000aaaa1111");
        Until(s.Window, () => s.Vm.Controller.FirmwareUpdateAvailable);
        var before = s.Rig.Connection!;
        s.Vm.Controller.UpdateFirmwareCommand.Execute(null);
        Reboot(s, before, ControllerConfigState.Stored, ControllerConfigCodec.Crc32(DiyBlob));

        Until(s.Window, () => s.Progress.IsDone);
        Assert.Equal("Updating firmware", s.Progress.Title);
        Assert.Equal($"Updated. The controller is running firmware {Catalog.BundledVersion}.",
                     In<TextBlock>(s.Overlay, "ProgressResult").Text);
        Assert.Equal(LinkTone.Good, s.Progress.ResultTone);
        var next = In<Button>(s.Overlay, "ProgressNextButton");
        Until(s.Window, () => next.IsEffectivelyVisible && next.IsEffectivelyEnabled);
        Assert.Equal("Test your controller now", next.Content);
        Assert.Equal("general", s.NavTag);

        Click(s, next);

        Assert.False(s.Progress.IsOpen);
        Assert.Equal("controller", s.NavTag);
        Assert.True(s.Vm.Controller.IsTesting);
        Assert.True(s.Rig.Runtime.InTestMode);
    }

    [AvaloniaFact]
    public void AFailedInstallOffersTryAgain()
    {
        using var s = Show();
        s.Flasher.FlashError = new Exception("the port closed");
        Install(s);

        Until(s.Window, () => s.Progress.IsDone);
        Assert.Equal("Install failed: the port closed. The controller won't work until the install succeeds. Try again.",
                     s.Progress.ResultText);
        Assert.Equal(LinkTone.Danger, s.Progress.ResultTone);
        var next = In<Button>(s.Overlay, "ProgressNextButton");
        Until(s.Window, () => next.IsEffectivelyEnabled);
        Assert.Equal("Try again", next.Content);

        Click(s, next);   // the same install again, without asking a second time

        Until(s.Window, () => s.Flasher.Calls.Count == 2 && s.Progress.IsDone);
        Assert.Equal(new[] { "flash COM3 Install", "flash COM3 Install" }, s.Flasher.Calls);
        Assert.False(s.Vm.Controller.ConfirmInstallPending);
        Assert.Equal("Try again", s.Progress.NextLabel);

        Escape(s);   // it ended: Escape closes it
        Assert.False(s.Progress.IsOpen);
        Assert.True(s.Banner.IsVisible);
        Assert.EndsWith("Try again.", In<TextBlock>(s.Banner, "BannerText").Text);
    }

    [AvaloniaFact]
    public void ASaveShowsSendingThenRestarting()
    {
        ProgressDialogViewModel.SavedHold = TimeSpan.FromMilliseconds(300);
        using var s = Show();
        s.Window.ShowPage("controller");
        Until(s.Window, () => s.Vm.Controller.CanEdit);
        s.Vm.Controller.SetSliderPin(1, 39);
        Settle(s.Window);
        var save = In<Button>(In<SaveBar>(s.Window, "SaveBarHost"), "SaveButton");
        Until(s.Window, () => save.IsEffectivelyEnabled);
        Click(s, save);

        Until(s.Window, () => s.Rig.Connection!.SentFrames.Any(f => f.Length > 3 && f[2] == 0x88));
        Until(s.Window, () => s.Overlay.IsVisible);
        Assert.Equal("Saving to the controller", In<TextBlock>(s.Overlay, "ProgressTitle").Text);
        Assert.Equal("Sending…", In<TextBlock>(s.Overlay, "ProgressStep").Text);
        Assert.True(In<TextBlock>(s.Overlay, "ProgressWarning").IsEffectivelyVisible);
        Assert.False(s.Progress.CloseCommand.CanExecute(null));

        var burst = s.Rig.Connection!.SentFrames.Last(f => f.Length > 3 && f[2] == 0x88);
        s.Rig.Connection.RaiseMessage(new ConfigResultMessage(burst[3], ConfigResultCode.Ok, 0xFF, 0xFF));
        Until(s.Window, () => s.Progress.Step == "Restarting…");
        Assert.Equal("Restarting…", In<TextBlock>(s.Overlay, "ProgressStep").Text);
        Escape(s);
        Assert.True(s.Progress.IsOpen);

        s.Rig.Connection.RaiseMessage(Hello("dev", ControllerConfigState.Stored,
                                            ControllerConfigCodec.Crc32(ControllerConfigCodec.Encode(s.Vm.Controller.Draft))));
        Until(s.Window, () => s.Progress.IsDone);
        Assert.Equal("Saved.", s.Progress.ResultText);
        Assert.Null(s.Progress.NextLabel);

        Until(s.Window, () => !s.Overlay.IsVisible);   // it closes by itself
        Assert.False(s.Vm.Controller.IsDirty);
        Assert.False(s.Banner.IsVisible);
    }

    /// <summary>An install erases the setup: the dialog says it's done, and once closed the banner
    /// asks how to start.</summary>
    [AvaloniaFact]
    public void CloseLeavesTheBannerForWhatRemains()
    {
        using var s = Show();
        var before = s.Rig.Connection!;
        Install(s);
        Reboot(s, before, ControllerConfigState.None, 0);

        Until(s.Window, () => s.Progress.IsDone);
        Assert.Equal($"Installed. The controller is running firmware {Catalog.BundledVersion}.", s.Progress.ResultText);
        Assert.Null(s.Progress.NextLabel);
        Until(s.Window, () => s.Vm.Controller.Banner.Kind == BannerKind.NotSetUpHasMirror);
        Assert.False(s.Banner.IsVisible);   // under the dialog

        Click(s, In<Button>(s.Overlay, "ProgressCloseButton"));

        Assert.False(s.Overlay.IsVisible);
        Assert.True(s.Banner.IsVisible);
        Assert.Equal("Not set up yet · Volco has a saved copy for it.", In<TextBlock>(s.Banner, "BannerText").Text);
        Assert.Equal("Choose how to start", In<Button>(s.Banner, "BannerAction").Content);
    }

    [AvaloniaFact]
    public void ClosingDeclinesTheOfferedTest()
    {
        using var s = Show(firmwareId: "0000aaaa1111");
        Until(s.Window, () => s.Vm.Controller.FirmwareUpdateAvailable);
        var before = s.Rig.Connection!;
        s.Vm.Controller.UpdateFirmwareCommand.Execute(null);
        Reboot(s, before, ControllerConfigState.Stored, ControllerConfigCodec.Crc32(DiyBlob));
        Until(s.Window, () => s.Progress.IsDone);
        Assert.Equal("Test your controller now", s.Progress.NextLabel);

        ClickScrim(s);   // it ended: the scrim closes it

        Assert.False(s.Progress.IsOpen);
        Assert.False(s.Vm.Controller.OfferTest);
        Until(s.Window, () => s.Vm.Hardware.Status.Level == StatusLevel.Fine);
        Assert.False(s.Banner.IsVisible);
        Assert.False(s.Vm.Controller.IsTesting);
    }
}
