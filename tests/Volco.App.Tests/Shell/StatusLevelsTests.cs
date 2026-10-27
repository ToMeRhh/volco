using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.ViewModels;
using Volco.App.ViewModels.Hardware;
using Volco.App.Views;
using Volco.App.Views.Hardware;
using Volco.App.Views.Pages;
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
using Ellipse = Avalonia.Controls.Shapes.Ellipse;

namespace Volco.App.Tests.Shell;

/// <summary>
/// Controller-page spec §5: the controller's status at three levels. All fine is a quiet line at
/// the bottom of the sidebar (only its dot in the compact layout) with the menu of the rare
/// actions; needs you is a banner across the top of the content area on every page, with the
/// state's one button. The actions that belong to the drawing go to the Controller page first,
/// and the Connection and Compare dialogs open over the whole window.
/// </summary>
public sealed class StatusLevelsTests : IDisposable
{
    public StatusLevelsTests() => StageTransitions.Enabled = false;

    public void Dispose() => StageTransitions.Enabled = false;

    private static readonly FirmwareCatalog Catalog = FirmwareCatalog.Load(MemoryFirmwareSource.Valid());
    private static readonly ControllerPreset Diy = ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId);
    private static readonly byte[] DiyBlob = ControllerConfigCodec.Encode(ControllerPresets.ApplyTo(new ControllerConfig(), Diy));
    private static readonly IndicatorKind[] DiyKinds = { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair };

    private sealed record Shown(SettingsWindow Window, TestRig Rig, SettingsViewModel Vm, FakeFlasher? Flasher) : IDisposable
    {
        public string NavTag => ((NavItem)Window.FindControl<ListBox>("NavList")!.SelectedItem!).Tag;
        public SidebarStatus Line => Window.FindControl<SidebarStatus>("StatusLine")!;
        public Button LineButton => In<Button>(Line, "StatusLineButton");
        public StatusBanner Banner => Window.FindControl<StatusBanner>("StatusBanner")!;
        public string LineText => In<TextBlock>(Line, "StatusText").Text!;
        public string BannerText => In<TextBlock>(Banner, "BannerText").Text!;
        public Button BannerAction => In<Button>(Banner, "BannerAction");

        public void Dispose()
        {
            Window.AllowClose = true;
            Window.Close();
            Vm.Dispose();
            Rig.Dispose();
        }
    }

    /// <summary>The settings window on General over Volco's DIY copy on COM3, greeted with
    /// <paramref name="state"/>, <paramref name="crc"/> (the copy's CRC when null) and
    /// <paramref name="firmwareId"/>; with <paramref name="firmware"/>, Volco's firmware is bundled.
    /// Without <paramref name="greet"/> the port opens and nothing answers.</summary>
    private static Shown Show(ControllerConfigState state = ControllerConfigState.Stored, uint? crc = null, double width = 1100,
                              string firmwareId = "dev", bool firmware = false, bool greet = true)
    {
        var options = TestRig.NoGrace with
        {
            Firmware = firmware ? Catalog : FirmwareCatalog.Empty,
            SilentAfter = greet ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(100),
        };
        var rig = new TestRig(options: options);
        var c = rig.Config.Config;
        c.Connection.Serial.ComPort = "COM3";
        c.Sliders = Enumerable.Range(0, 5).Select(i => new SliderConfig { Index = i, Type = SliderMappingType.None }).ToList();
        c.Buttons = Enumerable.Range(0, 3).Select(i => new ButtonConfig { Index = i }).ToList();
        c.ControllerConfigBlob = Convert.ToBase64String(DiyBlob);
        FakeFlasher? flasher = null;
        FirmwareService? service = null;
        if (firmware)
        {
            flasher = new FakeFlasher(() => rig.Runtime.IsSuspended);
            service = new FirmwareService(rig.Runtime, flasher, Catalog);
        }
        var vm = new SettingsViewModel(rig.Config, rig.Engine, rig.Runtime, new AvaloniaUiDispatcher(), service);
        var window = new SettingsWindow(vm, rig.Runtime) { Width = width, Height = 720 };
        window.Show();
        rig.Connection!.SetStatus(TransportStatus.Open);
        if (greet)
        {
            rig.Connection.RaiseMessage(new HelloMessage(ProtocolVersion.Current, ChipFamily.Esp32, firmwareId, state,
                                                         crc ?? ControllerConfigCodec.Crc32(DiyBlob), 5, DiyKinds));
            Until(window, () => vm.Controller.Banner.Kind is not (BannerKind.None or BannerKind.Reading) || vm.Controller.InSync);
            if (state == ControllerConfigState.Stored && crc is { } other && other != ControllerConfigCodec.Crc32(DiyBlob))
            {
                // The automatic look for a Wi-Fi fallback: the controller answers with another setup.
                Until(window, () => rig.Connection.SentFrames.Any(f => f.Length > 3 && f[2] == 0x87));
                var seq = rig.Connection.SentFrames.Last(f => f.Length > 3 && f[2] == 0x87)[3];
                byte[] theirs = { 0x32, 0x02, 0x08, 0x64 };
                rig.Connection.RaiseMessage(new ConfigDataMessage(seq, (ushort)theirs.Length, 0, ControllerConfigCodec.Crc32(theirs), theirs));
                Until(window, () => rig.Runtime.ControllerConfig.Operation == ConfigOperation.Idle);
            }
        }
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

    /// <summary>Waits for the banner's button to show <paramref name="label"/> and take a click.</summary>
    private static Button ActionShows(Shown s, string label)
    {
        Until(s.Window, () => s.BannerAction is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true }
                              && (string?)s.BannerAction.Content == label);
        return s.BannerAction;
    }

    // ── The three levels ─────────────────────────────────────────────────────────────────────

    /// <summary>The connected controller's hardware sits under the status line, on every page, and
    /// goes with the connection.</summary>
    [AvaloniaFact]
    public void TheStatusLineNamesTheControllersHardware()
    {
        using var s = Show();
        Until(s.Window, () => s.Vm.Controller.InSync);
        var hardware = In<TextBlock>(s.Line, "HardwareText");
        Assert.Equal("ESP32 · 5 sliders, 3 buttons", hardware.Text!.Replace('\u00A0', ' '));
        // It wraps whole rather than ending in "…", and only between its parts
        Assert.Equal(TextWrapping.Wrap, hardware.TextWrapping);
        Assert.Equal(TextTrimming.None, hardware.TextTrimming);
        Assert.Equal(2, hardware.Text!.Count(c => c == ' '));   // after the chip's dot and the comma
        Assert.True(hardware.IsEffectivelyVisible);
        double Top(Visual v) => v.TranslatePoint(new Point(0, 0), s.Line)!.Value.Y;
        Assert.True(Top(hardware) > Top(In<TextBlock>(s.Line, "StatusText")));

        s.Rig.Connection!.SetStatus(TransportStatus.Closed);   // unplugged
        Until(s.Window, () => !s.Vm.Controller.Connection.IsConnected);
        Assert.False(In<TextBlock>(s.Line, "HardwareText").IsVisible);
    }

    [AvaloniaFact]
    public void FineShowsOnlyTheStatusLine()
    {
        using var s = Show();
        Until(s.Window, () => s.Vm.Controller.InSync);
        Assert.IsType<GeneralPage>(s.Window.CurrentPage);
        Assert.Equal(StatusLevel.Fine, s.Vm.Hardware.Status.Level);
        Assert.Equal("Connected", s.LineText);
        Assert.True(In<TextBlock>(s.Line, "StatusText").IsEffectivelyVisible);
        Assert.Equal("Connected over USB (COM3) · Development firmware", ToolTip.GetTip(s.LineButton));
        Assert.False(s.Banner.IsVisible);

        s.Window.ShowPage("controller");
        Settle(s.Window);
        Assert.IsType<StagePage>(s.Window.CurrentPage);
        Assert.False(s.Banner.IsVisible);
        Assert.Equal("Connected", s.LineText);
    }

    [AvaloniaFact]
    public void NoAnswerShowsTheBannerOnEveryPage()
    {
        using var s = Show(firmware: true, greet: false);
        Until(s.Window, () => s.Vm.Hardware.Status.Level == StatusLevel.NeedsYou);
        Assert.Equal("general", s.NavTag);
        Assert.True(s.Banner.IsEffectivelyVisible);
        Assert.Equal("No controller answers on COM3 · It may not have Volco's firmware yet.", s.BannerText);
        Assert.Equal(s.BannerText, ToolTip.GetTip(In<TextBlock>(s.Banner, "BannerText")));
        ActionShows(s, "Check firmware");
        Assert.Equal("Not connected", s.LineText);   // a word or two; the tooltip has the rest
        // The banner is across the top of the content area, above the page.
        var page = s.Window.FindControl<TransitioningContentControl>("PageHost")!;
        Assert.True(s.Banner.TranslatePoint(new Point(0, s.Banner.Bounds.Height), s.Window)!.Value.Y
                    <= page.TranslatePoint(new Point(0, 0), s.Window)!.Value.Y);
        Assert.Equal(page.Bounds.Width, s.Banner.Bounds.Width);

        s.Window.ShowPage("controller");
        Settle(s.Window);
        Assert.IsType<StagePage>(s.Window.CurrentPage);
        Assert.True(s.Banner.IsEffectivelyVisible);
        Assert.Equal("No controller answers on COM3 · It may not have Volco's firmware yet.", s.BannerText);
        Assert.Equal("Check firmware", s.BannerAction.Content);
    }

    [AvaloniaFact]
    public void UpdateAvailableBannerUpdatesFromGeneral()
    {
        using var s = Show(firmwareId: "0000aaaa1111", firmware: true);
        Until(s.Window, () => s.Vm.Controller.FirmwareUpdateAvailable);
        Assert.Equal("Firmware update available · Your setup stays on the controller.", s.BannerText);
        var update = ActionShows(s, "Update");

        Click(s, update);

        Until(s.Window, () => s.Flasher!.Calls.Count == 1);
        Assert.Equal("flash COM3 Update", s.Flasher!.Calls.Single());
        Assert.Equal("general", s.NavTag);   // Update doesn't need the drawing
    }

    [AvaloniaFact]
    public void StatusLineMenuHasTheRareActions()
    {
        using var s = Show();
        Click(s, s.LineButton);
        var menu = Assert.IsType<MenuFlyout>(s.LineButton.Flyout);
        Assert.True(menu.IsOpen);
        var items = menu.Items.Select(i => i is MenuItem m ? (string)m.Header! : "―").ToArray();
        Assert.Equal(new[]
        {
            "Connection…", "Reconnect", "Disconnect", "―", "Check firmware", "Reinstall firmware…", "―",
            "Load a preset…", "Start over…",
        }, items);
        menu.Hide();
        Settle(s.Window);
    }

    [AvaloniaFact]
    public void NarrowSidebarShowsTheDot()
    {
        using var s = Show(width: 900);
        Settle(s.Window);
        Assert.Contains("compact", s.Window.Classes);
        var dot = In<Ellipse>(s.Line, "StatusDot");
        Assert.True(dot.IsEffectivelyVisible);
        Assert.False(In<TextBlock>(s.Line, "StatusText").IsEffectivelyVisible);
        // The dot sits under the nav icons, inside the 56 px sidebar.
        var x = dot.TranslatePoint(new Point(dot.Bounds.Width / 2, 0), s.Window)!.Value.X;
        Assert.InRange(x, 10, 46);

        s.Window.Width = 1100;
        Settle(s.Window);
        Assert.DoesNotContain("compact", s.Window.Classes);
        Assert.True(dot.IsEffectivelyVisible);
        Assert.True(In<TextBlock>(s.Line, "StatusText").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void TheSidebarCardIsGone()
    {
        using var s = Show();
        Assert.Null(s.Window.FindControl<Control>("Card"));
        Assert.DoesNotContain(s.Window.GetVisualDescendants(),
                              v => v is Control { Name: "CardName" or "MoreButton" or "CardDot" or "DetailButton" or "ConnectionFooter" });
        Assert.DoesNotContain(s.Window.GetVisualDescendants(), v => v is TextBlock { Text: "Volco controller" });
        // The status line sits above the version and links.
        var about = s.Window.FindControl<StackPanel>("AboutFooter")!;
        Assert.True(s.Line.TranslatePoint(new Point(0, s.Line.Bounds.Height), s.Window)!.Value.Y
                    <= about.TranslatePoint(new Point(0, 0), s.Window)!.Value.Y);
        Assert.Single(s.Window.GetVisualDescendants().OfType<SidebarStatus>());
        Assert.Single(s.Window.GetVisualDescendants().OfType<StatusBanner>());
    }

    // ── The banner's actions ─────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void FixItFromGeneralSwitchesToController()
    {
        // A damaged setup that is Volco's own copy: this firmware can't use it.
        using var s = Show(ControllerConfigState.Damaged);
        Until(s.Window, () => s.Vm.Controller.Banner.Kind == BannerKind.BrokenWithFirmware);
        Assert.Equal("general", s.NavTag);
        var fix = ActionShows(s, "Fix it");

        Click(s, fix);

        Assert.Equal("controller", s.NavTag);
        Assert.IsType<StagePage>(s.Window.CurrentPage);
    }

    [AvaloniaFact]
    public void LoadAPresetFromGeneralShowsTheChooserOnTheControllerPage()
    {
        using var s = Show();
        Until(s.Window, () => s.Vm.Controller.InSync && s.Vm.Hardware.Status.LoadPresetCommand.CanExecute(null));

        s.Vm.Hardware.Status.LoadPresetCommand.Execute(null);
        Settle(s.Window);

        Assert.Equal("controller", s.NavTag);
        var stage = Assert.IsType<StagePage>(s.Window.CurrentPage);
        Assert.True(In<Border>(stage, "PresetChooser").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void NotSetUpBannerGoesToTheChooser()
    {
        using var s = Show(ControllerConfigState.None, 0);
        Until(s.Window, () => s.Vm.Controller.Banner.Kind == BannerKind.NotSetUpHasMirror);
        Assert.Equal("Not set up yet · Volco has a saved copy for it.", s.BannerText);
        Assert.Equal("general", s.NavTag);

        Click(s, ActionShows(s, "Choose how to start"));

        Assert.Equal("controller", s.NavTag);
        Assert.True(s.Vm.Hardware.ShowChooser);
    }

    [AvaloniaFact]
    public void CompareOpensOverTheWindow()
    {
        using var s = Show(ControllerConfigState.Stored, 0x1234);
        Assert.Equal(BannerKind.DifferentSetup, s.Vm.Controller.Banner.Kind);
        var overlay = s.Window.FindControl<Border>("CompareOverlay")!;
        Assert.False(overlay.IsVisible);
        var compare = ActionShows(s, "Compare…");

        Click(s, compare);
        Assert.Equal("general", s.NavTag);   // Compare doesn't need the drawing
        Assert.True(overlay.IsEffectivelyVisible);
        Assert.Equal("The controller has a different setup", In<TextBlock>(overlay, "CompareTitle").Text);
        Escape(s);
        Assert.False(s.Vm.Hardware.IsCompareOpen);
        Assert.False(overlay.IsVisible);

        s.Vm.Hardware.OpenCompare();
        Settle(s.Window);
        Assert.True(overlay.IsVisible);
        s.Window.MouseDown(new Point(4, 4), MouseButton.Left);
        s.Window.MouseUp(new Point(4, 4), MouseButton.Left);
        Settle(s.Window);
        Assert.False(s.Vm.Hardware.IsCompareOpen);
    }

    // ── The menu's dialogs ───────────────────────────────────────────────────────────────────

    /// <summary>The menu item that asked has the focus, and is gone once the menu closes: a stand-in
    /// button takes the focus, then goes, while <paramref name="open"/> opens the dialog.</summary>
    private static void FromAMenuItem(Shown s, Action open)
    {
        var about = s.Window.FindControl<StackPanel>("AboutFooter")!;
        var item = new Button { Content = "Menu item" };
        about.Children.Add(item);
        Settle(s.Window);
        item.Focus();
        Assert.True(item.IsFocused);
        open();
        Settle(s.Window);
        about.Children.Remove(item);
        Settle(s.Window);
    }

    [AvaloniaFact]
    public void ConnectionDialogOpensOverGeneral()
    {
        using var s = Show();
        FromAMenuItem(s, () => s.Vm.Hardware.Status.OpenConnectionCommand.Execute(null));

        Assert.Equal("general", s.NavTag);
        Assert.True(s.Vm.Hardware.IsConnectionDialogOpen);
        var overlay = s.Window.FindControl<Border>("ConnectionOverlay")!;
        Assert.True(overlay.IsEffectivelyVisible);
        Until(s.Window, () => s.Window.FindControl<Border>("ConnectionCard")!.IsKeyboardFocusWithin);

        Escape(s);
        Assert.False(s.Vm.Hardware.IsConnectionDialogOpen);
        Assert.False(overlay.IsVisible);
        Assert.True(s.LineButton.IsFocused);   // the menu item is gone: the focus goes back to the status line

        s.Vm.Hardware.Status.OpenConnectionCommand.Execute(null);
        Settle(s.Window);
        Assert.True(overlay.IsVisible);
        s.Window.MouseDown(new Point(4, 300), MouseButton.Left);   // the scrim, over the sidebar
        s.Window.MouseUp(new Point(4, 300), MouseButton.Left);
        Settle(s.Window);
        Assert.False(s.Vm.Hardware.IsConnectionDialogOpen);
    }

    [AvaloniaFact]
    public void ReinstallAsksOverTheWindow()
    {
        using var s = Show();
        s.Vm.Controller.ConfirmInstallPending = true;   // Reinstall firmware… (the rig bundles no firmware)
        Settle(s.Window);

        var overlay = s.Window.FindControl<Border>("ReinstallOverlay")!;
        Assert.True(overlay.IsEffectivelyVisible);
        Assert.Equal("general", s.NavTag);
        Escape(s);
        Assert.False(s.Vm.Controller.ConfirmInstallPending);
        Assert.False(overlay.IsVisible);
    }

    [AvaloniaFact]
    public void ReinstallFromTheMenuGivesTheFocusBackToTheStatusLine()
    {
        using var s = Show();
        FromAMenuItem(s, () => s.Vm.Controller.ConfirmInstallPending = true);   // what the item asks
        Assert.True(s.Window.FindControl<Border>("ReinstallOverlay")!.IsVisible);

        Escape(s);

        Assert.False(s.Vm.Controller.ConfirmInstallPending);
        Assert.True(s.LineButton.IsFocused);
    }

    // ── The compact sidebar ──────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void CompactSidebarOnHoverShowsTheWholeLine()
    {
        using var s = Show(width: 900);
        var status = In<TextBlock>(s.Line, "StatusText");
        Assert.False(status.IsEffectivelyVisible);

        s.Window.MouseMove(new Point(20, 300));   // the mouse on the sidebar opens it over the page
        Settle(s.Window);
        Assert.Contains(":pointerover", s.Window.FindControl<Grid>("SidebarHost")!.Classes);
        Assert.True(status.IsEffectivelyVisible);

        s.Window.MouseMove(new Point(600, 300));
        Settle(s.Window);
        Assert.False(status.IsEffectivelyVisible);
        Assert.True(In<Ellipse>(s.Line, "StatusDot").IsEffectivelyVisible);
    }

    /// <summary>The status line's menu is a popup of its own: with the mouse on it, off the
    /// sidebar, the compact sidebar stays open ("pinned") so the line stays under the menu.</summary>
    [AvaloniaFact]
    public void CompactSidebarStaysOpenWhileTheMenuIs()
    {
        using var s = Show(width: 900);
        var sidebar = s.Window.FindControl<Grid>("SidebarHost")!;
        var status = In<TextBlock>(s.Line, "StatusText");
        var line = s.LineButton;
        s.Window.MouseMove(new Point(20, 300));
        Settle(s.Window);
        Assert.True(line.IsEffectivelyVisible);

        line.Flyout!.ShowAt(line);
        Settle(s.Window);
        Assert.True(line.Flyout.IsOpen);
        Assert.Contains("pinned", sidebar.Classes);

        s.Window.MouseMove(new Point(600, 300));   // the mouse leaves the sidebar for the menu
        Settle(s.Window);
        Assert.DoesNotContain(":pointerover", sidebar.Classes);
        Assert.Equal(168, sidebar.Bounds.Width);
        Assert.True(status.IsEffectivelyVisible);

        line.Flyout.Hide();
        Settle(s.Window);
        Assert.DoesNotContain("pinned", sidebar.Classes);
        Assert.Equal(56, sidebar.Bounds.Width);
        Assert.False(status.IsEffectivelyVisible);
    }

    // ── Over the window ──────────────────────────────────────────────────────────────────────

    /// <summary>Controller-page spec §4.4: the leave prompt covers the whole window, so neither the
    /// nav nor the status's actions change the draft under it.</summary>
    [AvaloniaFact]
    public void TheLeavePromptCoversTheSidebar()
    {
        using var s = Show();
        s.Window.ShowPage("controller");
        Until(s.Window, () => s.Vm.Controller.CanEdit);
        s.Vm.Controller.SetSliderPin(1, 39);
        Settle(s.Window);
        var draft = ControllerConfigCodec.Encode(s.Vm.Controller.Draft);
        s.Window.ShowPage("general");
        Settle(s.Window);
        Assert.NotNull(s.Vm.Hardware.LeavePrompt);
        var overlay = s.Window.FindControl<Border>("LeaveOverlay")!;
        Assert.True(overlay.IsEffectivelyVisible);
        Until(s.Window, () => s.Window.FindControl<Border>("LeaveCard")!.IsKeyboardFocusWithin);

        var line = s.LineButton;
        var nav = s.Window.FindControl<ListBox>("NavList")!;

        // No key reaches the status line or the nav, even with the focus forced onto them.
        line.Focus();
        s.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Settle(s.Window);
        Assert.False(line.Flyout!.IsOpen);
        nav.Focus();
        s.Window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Settle(s.Window);
        Assert.Equal("controller", s.NavTag);
        Assert.NotNull(s.Vm.Hardware.LeavePrompt);

        // The pointer doesn't reach them: a click where the status line is lands on the scrim, which is Stay.
        Click(s, line);
        Assert.Null(s.Vm.Hardware.LeavePrompt);
        Assert.False(line.Flyout!.IsOpen);
        Assert.Equal("controller", s.NavTag);

        // So does a click on the General entry while the prompt is up.
        s.Window.ShowPage("general");
        Settle(s.Window);
        Assert.NotNull(s.Vm.Hardware.LeavePrompt);
        var general = nav.GetVisualDescendants().OfType<ListBoxItem>().First();
        Click(s, general);
        Assert.Null(s.Vm.Hardware.LeavePrompt);
        Assert.Equal("controller", s.NavTag);

        Assert.Equal(draft, ControllerConfigCodec.Encode(s.Vm.Controller.Draft));
        Assert.True(s.Vm.Controller.IsDirty);
    }

    /// <summary>A status action that changes the drawing (Start over) closes the Controller page's
    /// dialog over it, and the device dialog over that.</summary>
    [AvaloniaFact]
    public void ADrawingActionClosesThePagesDialog()
    {
        using var s = Show();
        s.Window.ShowPage("controller");
        Until(s.Window, () => s.Vm.Controller.InSync && s.Vm.Hardware.Status.StartOverCommand.CanExecute(null));
        s.Vm.Controls.OpenTargetDialog(s.Vm.Sliders.Items[0]);
        Settle(s.Window);
        Assert.True(s.Vm.Controls.HasDialog);

        s.Vm.Hardware.Status.StartOverCommand.Execute(null);
        Settle(s.Window);

        Assert.Equal("controller", s.NavTag);
        Assert.False(s.Vm.Controls.HasDialog);
        Assert.False(s.Vm.Controls.HasDeviceDialog);
    }

    /// <summary>Escape with a dropdown open in a status dialog closes only the dropdown; the next
    /// one closes the dialog.</summary>
    [AvaloniaFact]
    public void EscapeClosesTheConnectionDialogsDropdownFirst()
    {
        using var s = Show();
        s.Vm.Hardware.Status.OpenConnectionCommand.Execute(null);
        Settle(s.Window);
        Assert.True(s.Vm.Hardware.IsConnectionDialogOpen);
        var combo = In<ComboBox>(s.Window.FindControl<Border>("ConnectionOverlay")!, "PortCombo");
        Assert.True(combo.IsEffectivelyVisible);
        combo.Focus(NavigationMethod.Pointer);
        combo.IsDropDownOpen = true;
        Settle(s.Window);

        Escape(s);
        Assert.False(combo.IsDropDownOpen);
        Assert.True(s.Vm.Hardware.IsConnectionDialogOpen);

        Escape(s);
        Assert.False(s.Vm.Hardware.IsConnectionDialogOpen);
    }
}
