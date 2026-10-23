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
    /// bundled, greeted by a controller running <paramref name="firmwareId"/> that holds the copy
    /// (or, without <paramref name="holdsCopy"/>, no setup at all). Without <paramref name="greet"/>
    /// the port opens and nothing answers.</summary>
    private static Shown Show(string firmwareId = "dev", bool greet = true, bool holdsCopy = true)
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
        if (greet)
            rig.Connection.RaiseMessage(holdsCopy ? Hello(firmwareId, ControllerConfigState.Stored, ControllerConfigCodec.Crc32(DiyBlob))
                                                  : Hello(firmwareId, ControllerConfigState.None, 0));
        Until(window, () => (!greet || (holdsCopy ? vm.Controller.InSync : vm.Controller.IsNotSetUp)) && vm.Controller.CanUseFirmware);
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
        using var s = Show(holdsCopy: false);
        var release = new TaskCompletionSource();
        s.Flasher.DuringFlash = () => release.Task;
        var before = s.Rig.Connection!;
        Install(s);

        Until(s.Window, () => s.Progress is { IsOpen: true, IsRunning: true, Progress: 0.5 });
        Assert.True(s.Progress.HasMeasure);   // writing: the bar shows how far
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
        Assert.Equal(ProgressNextStep.TryAgain, s.Progress.NextStep);

        Escape(s);   // it ended: Escape closes it
        Assert.False(s.Progress.IsOpen);
        Assert.True(s.Banner.IsVisible);
        Assert.EndsWith("Try again.", In<TextBlock>(s.Banner, "BannerText").Text);
    }

    /// <summary>A check on a board that never answered as Volco, which finds no Volco firmware,
    /// offers Install firmware, through the install confirmation, worded for a board that has none of
    /// Volco's firmware (not a warning: nothing of Volco's is erased).</summary>
    [AvaloniaFact]
    public void ACheckWithoutVolcoFirmwareOffersInstall()
    {
        using var s = Show(greet: false);
        s.Flasher.ProbeResult = new ProbeResult(ProbeKind.DifferentFirmware);
        s.Vm.Controller.CheckFirmwareCommand.Execute(null);
        Until(s.Window, () => s.Progress.IsOpen);
        Assert.Equal("Checking firmware", s.Progress.Title);
        Assert.False(s.Progress.HasMeasure);   // a check has no measure: the bar runs without one

        Until(s.Window, () => s.Progress.IsDone);
        Assert.Equal("This ESP32 doesn't have Volco's firmware yet.", s.Progress.ResultText);
        Assert.Equal(ProgressNextStep.InstallFirmware, s.Progress.NextStep);
        var next = In<Button>(s.Overlay, "ProgressNextButton");
        Until(s.Window, () => next.IsEffectivelyEnabled);
        Assert.Equal("Install firmware", next.Content);

        Click(s, next);   // the install confirmation asks first

        Assert.False(s.Progress.IsOpen);
        Assert.True(s.Vm.Controller.ConfirmInstallPending);
        var confirm = s.Window.FindControl<Border>("ReinstallOverlay")!;
        Assert.True(confirm.IsEffectivelyVisible);
        Assert.Equal("Install Volco's firmware on this ESP32? Anything else on it is replaced.",
                     In<TextBlock>(confirm, "ReinstallQuestion").Text);
        Assert.Equal("Install", In<Button>(confirm, "ConfirmReinstallButton").Content);
        Assert.Contains("Primary", In<Button>(confirm, "ConfirmReinstallButton").Classes);
        Assert.DoesNotContain("Danger", In<Button>(confirm, "ConfirmReinstallButton").Classes);
        Assert.Equal("probe COM3", s.Flasher.Calls.Single());

        Click(s, In<Button>(confirm, "ConfirmReinstallButton"));

        Until(s.Window, () => s.Flasher.Calls.Count == 2 && s.Progress.IsOpen);
        Assert.Equal("flash COM3 Install", s.Flasher.Calls[1]);
        Assert.Equal("Installing firmware", s.Progress.Title);
    }

    /// <summary>A board that answered as Volco this run and then probes as another build runs a
    /// different build of Volco's firmware: the next step is a reinstall, which warns that it erases
    /// the stored setup.</summary>
    [AvaloniaFact]
    public void ACheckFindingAnotherVolcoBuildOffersAReinstall()
    {
        using var s = Show();
        s.Flasher.ProbeResult = new ProbeResult(ProbeKind.DifferentFirmware);
        s.Vm.Controller.CheckFirmwareCommand.Execute(null);

        Until(s.Window, () => s.Progress.IsDone);
        Assert.Equal("This ESP32 runs a different build of Volco's firmware.", s.Progress.ResultText);
        Assert.Equal(ProgressNextStep.ReinstallFirmware, s.Progress.NextStep);
        var next = In<Button>(s.Overlay, "ProgressNextButton");
        Until(s.Window, () => next.IsEffectivelyEnabled);
        Assert.Equal("Reinstall firmware…", next.Content);

        Click(s, next);

        var confirm = s.Window.FindControl<Border>("ReinstallOverlay")!;
        Assert.True(confirm.IsEffectivelyVisible);
        Assert.StartsWith("Reinstalling erases the setup stored on the controller.", In<TextBlock>(confirm, "ReinstallQuestion").Text);
        Assert.Equal("Reinstall", In<Button>(confirm, "ConfirmReinstallButton").Content);
        Assert.Contains("Danger", In<Button>(confirm, "ConfirmReinstallButton").Classes);
    }

    /// <summary>On a board running Volco's firmware the confirmation is a reinstall's: it erases the
    /// stored setup.</summary>
    [AvaloniaFact]
    public void AReinstallAsksAboutTheSetup()
    {
        using var s = Show();
        s.Vm.Controller.InstallFirmwareCommand.Execute(null);   // the status menu's Reinstall firmware…
        Settle(s.Window);
        var confirm = s.Window.FindControl<Border>("ReinstallOverlay")!;
        Assert.True(confirm.IsEffectivelyVisible);
        Assert.Equal("Reinstalling erases the setup stored on the controller. Volco puts its copy back afterwards.",
                     In<TextBlock>(confirm, "ReinstallQuestion").Text);
        Assert.Equal("Reinstall", In<Button>(confirm, "ConfirmReinstallButton").Content);
        Assert.Contains("Danger", In<Button>(confirm, "ConfirmReinstallButton").Classes);
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
        // The save bar's Save went with the save bar: the focus goes back to the Controller page,
        // not to the sidebar's status line.
        Until(s.Window, () => s.Window.FocusManager?.GetFocusedElement() is Visual v
                              && v.GetVisualAncestors().Contains(s.Window.CurrentPage!));
        Assert.IsType<StagePage>(s.Window.CurrentPage);
    }

    /// <summary>An install leaves a controller that didn't hold Volco's copy without a setup: the
    /// dialog says it's done, offers to choose how to start, and once closed the banner asks how to
    /// start.</summary>
    [AvaloniaFact]
    public void CloseLeavesTheBannerForWhatRemains()
    {
        using var s = Show(holdsCopy: false);
        var before = s.Rig.Connection!;
        Install(s);
        Reboot(s, before, ControllerConfigState.None, 0);

        Until(s.Window, () => s.Progress.IsDone);
        Assert.Equal($"Installed. The controller is running firmware {Catalog.BundledVersion}.", s.Progress.ResultText);
        Assert.Equal("Choose how to start", s.Progress.NextLabel);
        Until(s.Window, () => s.Vm.Controller.Banner.Kind == BannerKind.NotSetUpHasMirror);
        Assert.False(s.Banner.IsVisible);   // under the dialog

        Click(s, In<Button>(s.Overlay, "ProgressCloseButton"));

        Assert.False(s.Overlay.IsVisible);
        Assert.True(s.Banner.IsVisible);
        Assert.Equal("Not set up yet · Volco has a saved copy for it.", In<TextBlock>(s.Banner, "BannerText").Text);
        Assert.Equal("Choose how to start", In<Button>(s.Banner, "BannerAction").Content);
    }

    /// <summary>After an install the controller isn't set up: the dialog's next step opens the
    /// setup chooser on the Controller page.</summary>
    [AvaloniaFact]
    public void AnInstallOffersChooseHowToStart()
    {
        using var s = Show(holdsCopy: false);
        var before = s.Rig.Connection!;
        Install(s);
        Reboot(s, before, ControllerConfigState.None, 0);

        Until(s.Window, () => s.Progress.IsDone);
        Assert.Equal(ProgressNextStep.ChooseHowToStart, s.Progress.NextStep);
        var next = In<Button>(s.Overlay, "ProgressNextButton");
        Until(s.Window, () => next.IsEffectivelyVisible && next.IsEffectivelyEnabled);
        Assert.Equal("Choose how to start", next.Content);
        Assert.Equal("general", s.NavTag);

        Click(s, next);

        Assert.False(s.Progress.IsOpen);
        Assert.Equal("controller", s.NavTag);
        Until(s.Window, () => s.Vm.Hardware.ShowChooser);
    }

    /// <summary>A reinstall over a controller that held Volco's copy puts the copy back at once (the
    /// same controller, so the same wiring): no chooser, and the next step is the test.</summary>
    [AvaloniaFact]
    public void AReinstallPutsTheSetupBack()
    {
        using var s = Show();
        var before = s.Rig.Connection!;
        Install(s);
        Reboot(s, before, ControllerConfigState.None, 0);

        Until(s.Window, () => s.Rig.Connection!.SentFrames.Any(f => f.Length > 3 && f[2] == 0x88));
        Until(s.Window, () => s.Progress is { IsOpen: true, IsRunning: true, Title: "Restoring your setup" });
        Assert.Equal("Restoring your setup", In<TextBlock>(s.Overlay, "ProgressTitle").Text);
        Assert.False(s.Vm.Hardware.ShowChooser);

        var burst = s.Rig.Connection!.SentFrames.Last(f => f.Length > 3 && f[2] == 0x88);
        s.Rig.Connection.RaiseMessage(new ConfigResultMessage(burst[3], ConfigResultCode.Ok, 0xFF, 0xFF));
        s.Rig.Connection.RaiseMessage(Hello(Catalog.BundledVersion!, ControllerConfigState.Stored, ControllerConfigCodec.Crc32(DiyBlob)));

        Until(s.Window, () => s.Progress.IsDone);
        Assert.Equal("Your setup is back on the controller.", s.Progress.ResultText);
        Assert.Equal(LinkTone.Good, s.Progress.ResultTone);
        Assert.Equal(ProgressNextStep.TestNow, s.Progress.NextStep);
        Assert.True(s.Vm.Controller.InSync);
        Assert.Equal(new[] { "flash COM3 Install" }, s.Flasher.Calls);
    }

    /// <summary>Development firmware is never offered an update on its own, but the status menu's
    /// Update firmware puts Volco's bundled build on it and keeps the stored setup.</summary>
    [AvaloniaFact]
    public void TheMenuUpdatesDevelopmentFirmware()
    {
        using var s = Show(firmwareId: "dev");
        var status = s.Vm.Hardware.Status;
        Assert.True(status.OffersUpdate);
        Assert.False(s.Vm.Controller.FirmwareUpdateAvailable);
        var before = s.Rig.Connection!;
        Assert.True(status.UpdateFirmwareCommand.CanExecute(null));
        status.UpdateFirmwareCommand.Execute(null);
        Reboot(s, before, ControllerConfigState.Stored, ControllerConfigCodec.Crc32(DiyBlob));

        Until(s.Window, () => s.Progress.IsDone);
        Assert.Equal(new[] { "flash COM3 Update" }, s.Flasher.Calls);
        Assert.Equal($"Updated. The controller is running firmware {Catalog.BundledVersion}.", s.Progress.ResultText);
        Assert.Equal(ProgressNextStep.TestNow, s.Progress.NextStep);
        Assert.True(s.Vm.Controller.InSync);
        Assert.False(status.OffersUpdate);   // the bundled build now: the menu has no Update
    }

    /// <summary>A release build gets its update from the card, not the menu.</summary>
    [AvaloniaFact]
    public void TheMenuHasNoUpdateForReleaseFirmware()
    {
        using var s = Show(firmwareId: "0000aaaa1111");
        Assert.False(s.Vm.Hardware.Status.OffersUpdate);
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
