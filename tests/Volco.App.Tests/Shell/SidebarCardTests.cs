using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.ViewModels;
using Volco.App.Views;
using Volco.App.Views.Pages;
using Volco.App.Views.Stage;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Volco.Core.Tests.Runtime;
using Xunit;
using Button = Avalonia.Controls.Button;
using CardView = Volco.App.Views.Hardware.DeviceCard;

namespace Volco.App.Tests.Shell;

/// <summary>
/// Controller-page spec §5: the device card is in the sidebar on every page, only its dot in the
/// compact layout; the actions that belong to the drawing go to the Controller page first, and
/// the Connection and Compare dialogs open over the whole window.
/// </summary>
public sealed class SidebarCardTests : IDisposable
{
    public SidebarCardTests() => StageTransitions.Enabled = false;

    public void Dispose() => StageTransitions.Enabled = false;

    private static readonly ControllerPreset Diy = ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId);
    private static readonly byte[] DiyBlob = ControllerConfigCodec.Encode(ControllerPresets.ApplyTo(new ControllerConfig(), Diy));
    private static readonly IndicatorKind[] DiyKinds = { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair };

    private sealed record Shown(SettingsWindow Window, TestRig Rig, SettingsViewModel Vm) : IDisposable
    {
        public string NavTag => ((NavItem)Window.FindControl<ListBox>("NavList")!.SelectedItem!).Tag;
        public CardView Card => Window.FindControl<CardView>("Card")!;

        public void Dispose()
        {
            Window.AllowClose = true;
            Window.Close();
            Vm.Dispose();
            Rig.Dispose();
        }
    }

    /// <summary>The settings window on General over Volco's DIY copy, greeted with
    /// <paramref name="state"/> and <paramref name="crc"/> (the copy's CRC when null).</summary>
    private static Shown Show(ControllerConfigState state = ControllerConfigState.Stored, uint? crc = null, double width = 1100)
    {
        var rig = new TestRig();
        var c = rig.Config.Config;
        c.Sliders = Enumerable.Range(0, 5).Select(i => new SliderConfig { Index = i, Type = SliderMappingType.None }).ToList();
        c.Buttons = Enumerable.Range(0, 3).Select(i => new ButtonConfig { Index = i }).ToList();
        c.ControllerConfigBlob = Convert.ToBase64String(DiyBlob);
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var window = new SettingsWindow(vm, rig.Runtime) { Width = width, Height = 720 };
        window.Show();
        rig.Connection!.SetStatus(TransportStatus.Open);
        rig.Connection.RaiseMessage(new HelloMessage(ProtocolVersion.Current, ChipFamily.Esp32, "dev", state,
                                                     crc ?? ControllerConfigCodec.Crc32(DiyBlob), 5, DiyKinds));
        Until(window, () => vm.Controller.Banner.Kind is not (BannerKind.None or BannerKind.Reading) || vm.Controller.InSync);
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

    [AvaloniaFact]
    public void CardIsInTheSidebarOnEveryPage()
    {
        using var s = Show();
        Assert.IsType<GeneralPage>(s.Window.CurrentPage);
        var name = In<TextBlock>(s.Card, "CardName");
        Assert.Equal("Volco controller", name.Text);
        Assert.True(name.IsEffectivelyVisible);
        Assert.StartsWith("Connected over USB", In<TextBlock>(s.Card, "StatusText").Text);

        s.Window.ShowPage("controller");
        Settle(s.Window);
        var stage = Assert.IsType<StagePage>(s.Window.CurrentPage);
        Assert.True(In<TextBlock>(s.Card, "CardName").IsEffectivelyVisible);
        Assert.Equal("Volco controller", In<TextBlock>(s.Card, "CardName").Text);
        // One card: the Controller page's side panel no longer has its own.
        Assert.Empty(stage.GetVisualDescendants().OfType<CardView>());
        Assert.Single(s.Window.GetVisualDescendants().OfType<CardView>());
    }

    [AvaloniaFact]
    public void OldFooterIsGone()
    {
        using var s = Show();
        Assert.Null(s.Window.FindControl<StackPanel>("ConnectionFooter"));
        Assert.DoesNotContain(s.Window.GetVisualDescendants(), v => v is Control { Name: "ConnectionFooter" or "FooterText" or "FooterDot" });
        // The card sits above the version and links.
        var about = s.Window.FindControl<StackPanel>("AboutFooter")!;
        Assert.True(s.Card.TranslatePoint(new Point(0, s.Card.Bounds.Height), s.Window)!.Value.Y
                    <= about.TranslatePoint(new Point(0, 0), s.Window)!.Value.Y);
    }

    [AvaloniaFact]
    public void CompactShowsTheDotOnly()
    {
        using var s = Show(width: 900);
        Settle(s.Window);
        Assert.Contains("compact", s.Window.Classes);
        Assert.True(In<Avalonia.Controls.Shapes.Ellipse>(s.Card, "CardDot").IsEffectivelyVisible);
        Assert.False(In<TextBlock>(s.Card, "StatusText").IsEffectivelyVisible);

        s.Window.Width = 1100;
        Settle(s.Window);
        Assert.DoesNotContain("compact", s.Window.Classes);
        Assert.False(In<Avalonia.Controls.Shapes.Ellipse>(s.Card, "CardDot").IsEffectivelyVisible);
        Assert.True(In<TextBlock>(s.Card, "StatusText").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void FixItFromGeneralSwitchesToController()
    {
        // A damaged setup that is Volco's own copy: this firmware can't use it.
        using var s = Show(ControllerConfigState.Damaged);
        Until(s.Window, () => s.Vm.Controller.Banner.Kind == BannerKind.BrokenWithFirmware);
        Assert.Equal("general", s.NavTag);
        var fix = In<Button>(s.Card, "ActionButton");
        Until(s.Window, () => fix.IsEffectivelyVisible && fix.IsEffectivelyEnabled);
        Assert.Equal("Fix it", fix.Content);

        Click(s, fix);

        Assert.Equal("controller", s.NavTag);
        Assert.IsType<StagePage>(s.Window.CurrentPage);
    }

    [AvaloniaFact]
    public void LoadAPresetFromGeneralShowsTheChooserOnTheControllerPage()
    {
        using var s = Show();
        Until(s.Window, () => s.Vm.Controller.InSync && s.Vm.Hardware.Card.LoadPresetCommand.CanExecute(null));

        s.Vm.Hardware.Card.LoadPresetCommand.Execute(null);
        Settle(s.Window);

        Assert.Equal("controller", s.NavTag);
        var stage = Assert.IsType<StagePage>(s.Window.CurrentPage);
        Assert.True(In<Border>(stage, "PresetChooser").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void NotSetUpDetailGoesToTheChooser()
    {
        using var s = Show(ControllerConfigState.None, 0);
        Until(s.Window, () => s.Vm.Controller.Banner.Kind == BannerKind.NotSetUpHasMirror);
        var detail = In<TextBlock>(s.Card, "DetailText");
        Assert.Equal("Choose how to start.", detail.Text);
        Assert.Equal("general", s.NavTag);

        Click(s, In<Button>(s.Card, "DetailButton"));

        Assert.Equal("controller", s.NavTag);
        Assert.True(s.Vm.Hardware.ShowChooser);
    }

    [AvaloniaFact]
    public void ConnectionDialogOpensOverGeneral()
    {
        using var s = Show();
        var status = In<Button>(s.Card, "StatusButton");
        status.Focus();
        Click(s, status);

        Assert.Equal("general", s.NavTag);
        Assert.True(s.Vm.Hardware.IsConnectionDialogOpen);
        var overlay = s.Window.FindControl<Border>("ConnectionOverlay")!;
        Assert.True(overlay.IsEffectivelyVisible);
        Until(s.Window, () => s.Window.FindControl<Border>("ConnectionCard")!.IsKeyboardFocusWithin);

        Escape(s);
        Assert.False(s.Vm.Hardware.IsConnectionDialogOpen);
        Assert.False(overlay.IsVisible);
        Assert.True(status.IsFocused);

        Click(s, status);
        Assert.True(overlay.IsVisible);
        s.Window.MouseDown(new Point(4, 300), MouseButton.Left);   // the scrim, over the sidebar
        s.Window.MouseUp(new Point(4, 300), MouseButton.Left);
        Settle(s.Window);
        Assert.False(s.Vm.Hardware.IsConnectionDialogOpen);
    }

    [AvaloniaFact]
    public void CompareOpensOverTheWindow()
    {
        using var s = Show(ControllerConfigState.Stored, 0x1234);
        Assert.Equal(BannerKind.DifferentSetup, s.Vm.Controller.Banner.Kind);
        var overlay = s.Window.FindControl<Border>("CompareOverlay")!;
        Assert.False(overlay.IsVisible);
        var compare = In<Button>(s.Card, "ActionButton");
        Until(s.Window, () => compare.IsEffectivelyVisible && compare.IsEffectivelyEnabled);
        Assert.Equal("Compare…", compare.Content);

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

    [AvaloniaFact]
    public void ReinstallAsksOverTheWindow()
    {
        using var s = Show();
        s.Vm.Controller.ConfirmInstallPending = true;   // ⋯ Reinstall firmware… (the rig bundles no firmware)
        Settle(s.Window);

        var overlay = s.Window.FindControl<Border>("ReinstallOverlay")!;
        Assert.True(overlay.IsEffectivelyVisible);
        Assert.Equal("general", s.NavTag);
        Escape(s);
        Assert.False(s.Vm.Controller.ConfirmInstallPending);
        Assert.False(overlay.IsVisible);
    }

    [AvaloniaFact]
    public void ReinstallFromTheMenuGivesTheFocusBackToMore()
    {
        using var s = Show();
        var more = In<Button>(s.Card, "MoreButton");
        // The menu item that asked has the focus, and is gone once the menu closes.
        var about = s.Window.FindControl<StackPanel>("AboutFooter")!;
        var item = new Button { Content = "Reinstall firmware…" };
        about.Children.Add(item);
        Settle(s.Window);
        item.Focus();
        Assert.True(item.IsFocused);
        s.Vm.Controller.ConfirmInstallPending = true;   // what the item asks (the rig bundles no firmware)
        Settle(s.Window);
        about.Children.Remove(item);
        Settle(s.Window);
        Assert.True(s.Window.FindControl<Border>("ReinstallOverlay")!.IsVisible);

        Escape(s);

        Assert.False(s.Vm.Controller.ConfirmInstallPending);
        Assert.True(more.IsFocused);   // the menu item is gone: the focus goes back to ⋯
    }

    [AvaloniaFact]
    public void CompactSidebarOnHoverShowsTheWholeCard()
    {
        using var s = Show(width: 900);
        var dot = In<Avalonia.Controls.Shapes.Ellipse>(s.Card, "CardDot");
        var status = In<TextBlock>(s.Card, "StatusText");
        Assert.True(dot.IsEffectivelyVisible);

        s.Window.MouseMove(new Point(20, 300));   // the mouse on the sidebar opens it over the page
        Settle(s.Window);
        Assert.Contains(":pointerover", s.Window.FindControl<Grid>("SidebarHost")!.Classes);
        Assert.True(status.IsEffectivelyVisible);
        Assert.False(dot.IsEffectivelyVisible);

        s.Window.MouseMove(new Point(600, 300));
        Settle(s.Window);
        Assert.True(dot.IsEffectivelyVisible);
        Assert.False(status.IsEffectivelyVisible);
    }

    /// <summary>Controller-page spec §4.4: the leave prompt covers the whole window, so neither the
    /// nav nor the card's actions change the draft under it.</summary>
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

        // The pointer doesn't reach the card's ⋯ or the nav: the scrim is over them.
        var more = In<Button>(s.Card, "MoreButton");
        var nav = s.Window.FindControl<ListBox>("NavList")!;
        foreach (var target in new Control[] { more, nav })
        {
            var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), s.Window)!.Value;
            var hit = s.Window.InputHitTest(point) as Visual;
            Assert.False(ReferenceEquals(hit, target) || target.IsVisualAncestorOf(hit),
                         $"{target.Name} is reachable at {point}: the hit is {hit?.GetType().Name} {(hit as Control)?.Name}");
        }

        // No key reaches them either, even with the focus forced onto them.
        more.Focus();
        s.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Settle(s.Window);
        Assert.False(more.Flyout!.IsOpen);
        nav.Focus();
        s.Window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Settle(s.Window);
        Assert.Equal("controller", s.NavTag);
        Assert.NotNull(s.Vm.Hardware.LeavePrompt);

        // A click where ⋯ is is Stay.
        Click(s, more);
        Assert.Null(s.Vm.Hardware.LeavePrompt);
        Assert.False(more.Flyout!.IsOpen);
        Assert.Equal("controller", s.NavTag);
        Assert.Equal(draft, ControllerConfigCodec.Encode(s.Vm.Controller.Draft));
        Assert.True(s.Vm.Controller.IsDirty);
    }
}
