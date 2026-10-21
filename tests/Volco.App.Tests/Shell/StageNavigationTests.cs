using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.Controls;
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
using Volco.Core.Tests.Runtime;
using Xunit;
using Button = Avalonia.Controls.Button;

namespace Volco.App.Tests.Shell;

/// <summary>
/// The settings window's Controller page (controller-page spec §4): General crossfades in, and
/// leaving the Controller page with unsaved changes asks first (§4.4). Also the title row's
/// buttons (§4.2) and the inline confirmation's styles.
/// </summary>
public sealed class StageNavigationTests
{
    private static readonly ControllerPreset Diy = ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId);
    private static readonly IndicatorKind[] DiyKinds = { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair };

    private sealed record Shown(SettingsWindow Window, TestRig Rig, SettingsViewModel Vm) : IDisposable
    {
        public StagePage Stage => Assert.IsType<StagePage>(Window.CurrentPage);
        public FakeControllerConnection Conn => Rig.Connection!;
        public string NavTag => ((NavItem)Window.FindControl<ListBox>("NavList")!.SelectedItem!).Tag;

        public void Dispose()
        {
            Window.AllowClose = true;
            Window.Close();
            Vm.Dispose();
            Rig.Dispose();
        }
    }

    /// <summary>The settings window on the Controller page over the DIY setup (5 sliders; buttons LED, LED,
    /// pair), in sync with the controller so the draft can be edited.</summary>
    /// <param name="extraButtons">Buttons with no light after the DIY ones, so the drawing scrolls.</param>
    private static Shown Show(double height = 720, int extraButtons = 0)
    {
        var config = ControllerPresets.ApplyTo(new ControllerConfig(), Diy);
        uint[] free = { 13, 15, 16, 17, 22, 23, 27, 2 };
        for (int i = 0; i < extraButtons; i++) config.Buttons.Add(new Volco.Core.Controller.Proto.Button { Pin = free[i] });
        var blob = ControllerConfigCodec.Encode(config);
        var kinds = DiyKinds.Concat(Enumerable.Repeat(IndicatorKind.None, extraButtons)).ToArray();
        var rig = new TestRig();
        var c = rig.Config.Config;
        c.Sliders = Enumerable.Range(0, 5).Select(i => new SliderConfig { Index = i, Type = SliderMappingType.None }).ToList();
        c.Buttons = Enumerable.Range(0, kinds.Length).Select(i => new ButtonConfig { Index = i }).ToList();
        c.ControllerConfigBlob = Convert.ToBase64String(blob);
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var window = new SettingsWindow(vm, rig.Runtime) { Width = 1100, Height = height };
        window.Show();
        rig.Connection!.SetStatus(TransportStatus.Open);
        rig.Connection.RaiseMessage(new HelloMessage(ProtocolVersion.Current, ChipFamily.Esp32, "dev", ControllerConfigState.Stored,
                                                     ControllerConfigCodec.Crc32(blob), 5, kinds));
        window.ShowPage("controller");
        Until(window, () => vm.Controller.CanEdit && vm.Hardware.CanStartTest);
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
        control.BringIntoView();
        Settle(s.Window);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), s.Window)!.Value;
        s.Window.MouseDown(point, MouseButton.Left);
        s.Window.MouseUp(point, MouseButton.Left);
        Settle(s.Window);
    }

    /// <summary>A click: the button's Click handlers, then its command (as Button.OnClick does).</summary>
    private static void Press(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (button.Command is { } command && command.CanExecute(button.CommandParameter)) command.Execute(button.CommandParameter);
    }

    /// <summary>Every tile's rectangle in the stage.</summary>
    private static List<Rect> TileRects(StagePage stage)
        => stage.Tiles().Select(t => new Rect(t.TranslatePoint(new Point(0, 0), stage)!.Value, t.Bounds.Size)).ToList();

    private static bool AnyWrite(Shown s) => s.Conn.SentFrames.Any(f => f.Length > 3 && f[2] == 0x88);

    /// <summary>Answers the last write burst with <paramref name="code"/>; OK also confirms with a Hello.</summary>
    private static void Answer(Shown s, ConfigResultCode code)
    {
        Until(s.Window, () => AnyWrite(s));
        var burst = s.Conn.SentFrames.Last(f => f.Length > 3 && f[2] == 0x88);
        s.Conn.RaiseMessage(new ConfigResultMessage(burst[3], code, 0xFF, 0xFF));
        if (code == ConfigResultCode.Ok)
            s.Conn.RaiseMessage(new HelloMessage(ProtocolVersion.Current, ChipFamily.Esp32, "dev", ControllerConfigState.Stored,
                                                 ControllerConfigCodec.Crc32(ControllerConfigCodec.Encode(s.Vm.Controller.Draft)), 5, DiyKinds));
    }

    /// <summary>An unsaved pin change, then a click on the General entry: the prompt opens.</summary>
    private static void LeaveDirty(Shown s, string tag = "general")
    {
        s.Vm.Controller.SetSliderPin(1, 39);
        Settle(s.Window);
        s.Window.ShowPage(tag);
        Settle(s.Window);
    }

    // ── One page ─────────────────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void GeneralCrossfades()
    {
        StageTransitions.Enabled = true;
        try
        {
            using var s = Show();
            var host = s.Window.FindControl<TransitioningContentControl>("PageHost")!;
            s.Window.ShowPage("general");
            var fade = Assert.IsType<CrossFade>(host.PageTransition);
            Assert.Equal(TimeSpan.FromMilliseconds(150), fade.Duration);
            Assert.IsType<GeneralPage>(host.Content);
        }
        finally { StageTransitions.Enabled = false; }
    }

    [AvaloniaFact]
    public void TrayOpensTheControllerPage()
    {
        using var s = Show();
        s.Window.ShowPage("general");
        Settle(s.Window);
        Assert.IsType<GeneralPage>(s.Window.CurrentPage);

        s.Window.ShowPage("controller");   // the tray's "Update controller firmware…" and the nav dot
        Settle(s.Window);
        Assert.Equal("controller", s.NavTag);
        Assert.Equal("Controller", In<TextBlock>(s.Stage, "PageTitle").Text);
        Assert.Same(s.Vm.Hardware, s.Stage.Hardware);
        Assert.IsType<HardwareInspectorHost>(s.Stage.HardwareSidePanel);
    }

    [AvaloniaFact]
    public void TheControllerEntrysDotFollowsTheVm()
    {
        using var s = Show();
        var item = s.Window.FindControl<ListBox>("NavList")!.Items.Cast<NavItem>().Single(n => n.Tag == "controller");
        Assert.False(item.ShowDot);
        s.Vm.ControllerNavDot = true;
        Assert.True(item.ShowDot);
    }

    [AvaloniaFact]
    public void LeavingForGeneralOrHidingEndsTheTest()
    {
        using var s = Show();
        s.Vm.Hardware.StartTestCommand.Execute(null);
        Until(s.Window, () => s.Rig.Runtime.InTestMode);
        s.Window.ShowPage("general");
        Settle(s.Window);
        Assert.False(s.Rig.Runtime.InTestMode);

        s.Window.ShowPage("controller");
        Until(s.Window, () => s.Vm.Hardware.CanStartTest);
        s.Vm.Hardware.StartTestCommand.Execute(null);
        Until(s.Window, () => s.Rig.Runtime.InTestMode);
        s.Window.Close();   // hides
        Settle(s.Window);
        Assert.False(s.Window.IsVisible);
        Assert.False(s.Rig.Runtime.InTestMode);
    }

    [AvaloniaFact]
    public void TileBoundsDontChangeWhenATestStarts()
    {
        using var s = Show();
        var rects = TileRects(s.Stage);
        s.Vm.Hardware.StartTestCommand.Execute(null);
        Until(s.Window, () => s.Vm.Hardware.IsTesting);
        Assert.Contains("testing", In<Border>(s.Stage, "Case").Classes);
        Assert.Equal(rects, TileRects(s.Stage));
    }

    // ── The title row (spec §4.2) ────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void TitleRowHasAddControllerSettingsAndTest()
    {
        using var s = Show();
        var actions = In<TransitioningContentControl>(s.Stage, "TitleActions");
        Assert.IsType<HardwareTitleActions>(actions.Content);

        // + Add › Slider and › Button add to the draft.
        var add = In<Button>(actions, "AddButton");
        var menu = Assert.IsType<MenuFlyout>(add.Flyout);
        menu.ShowAt(add);
        Settle(s.Window);
        Assert.Equal(new[] { "Slider", "Button" }, menu.Items.OfType<MenuItem>().Select(i => (string)i.Header!));
        menu.Items.OfType<MenuItem>().First().Command!.Execute(null);
        menu.Items.OfType<MenuItem>().Last().Command!.Execute(null);
        menu.Hide();
        Settle(s.Window);
        Assert.Equal(6, s.Vm.Controller.Draft.Sliders.Count);
        Assert.Equal(4, s.Vm.Controller.Draft.Buttons.Count);
        Assert.Equal(6, s.Stage.Tiles().OfType<SliderTile>().Count());

        // The chip shows the controller's settings in the inspector.
        var chip = In<Button>(actions, "ControllerSettingsChip");
        Click(s, chip);
        Assert.IsType<ControllerSettingsViewModel>(s.Vm.Hardware.Inspected);
        Assert.Contains("selected", chip.Classes);

        // Test, then Stop test.
        Assert.True(In<Button>(actions, "TestButton").IsVisible);
        Assert.False(In<Button>(actions, "StopTestButton").IsVisible);
        s.Vm.Controller.RevertCommand.Execute(null);
        Until(s.Window, () => s.Vm.Hardware.CanStartTest);
        Click(s, In<Button>(actions, "TestButton"));
        Until(s.Window, () => s.Rig.Runtime.InTestMode);
        Assert.False(In<Button>(actions, "TestButton").IsVisible);
        Assert.False(add.IsEffectivelyEnabled);   // editing is paused
        Click(s, In<Button>(actions, "StopTestButton"));
        Until(s.Window, () => !s.Rig.Runtime.InTestMode);
    }

    [AvaloniaFact]
    public void EditingAPinInTheInspectorShowsTheSaveBar()
    {
        using var s = Show();
        var bar = In<SaveBar>(s.Stage, "SaveBarHost");
        Assert.False(bar.IsVisible);
        s.Vm.Hardware.Sections.SliderHardwareOpen = true;
        s.Vm.Controls.Select(s.Vm.Sliders.Items[0]);
        Settle(s.Window);
        var field = In<PinField>(s.Stage, "PinField");   // in the inspector's Hardware section
        Press(In<Button>(field, "ValueButton"));
        Settle(s.Window);
        Press(In<Popup>(field, "ListPopup").Child!.GetVisualDescendants().OfType<Button>()
            .First(b => b.Name == "PinRowButton" && ((PinRowViewModel)b.DataContext!).Pin == 39));
        Settle(s.Window);

        Assert.Equal(39u, s.Vm.Controller.Draft.Sliders[0].Pin);
        Assert.True(bar.IsVisible);
        Assert.Equal("1 change not saved", In<TextBlock>(bar, "SaveBarText").Text);
    }

    [AvaloniaFact]
    public void OnlyARemoveConfirmsInRed()
    {
        using var s = Show();
        var host = s.Stage.FindControl<Volco.App.Views.Hardware.InlineConfirmation>("ConfirmHost")!;   // the side panel's bottom
        s.Vm.Controller.AskRemoveSlider(0);
        Settle(s.Window);
        var confirm = In<Button>(host, "ConfirmButton");
        Assert.Contains("Danger", confirm.Classes);
        Assert.DoesNotContain("Primary", confirm.Classes);
        s.Vm.Controller.Confirmation!.CancelCommand.Execute(null);

        s.Vm.Controller.LoadPreset(ControllerPresets.All.Single(p => p.Id == ControllerPresets.BlankId));
        Settle(s.Window);
        Assert.Equal("Replace the current hardware with Blank?", In<TextBlock>(host, "ConfirmText").Text);
        Assert.DoesNotContain("Danger", confirm.Classes);
        Assert.Contains("Primary", confirm.Classes);
    }

    [AvaloniaFact]
    public void TheCardsDotFollowsTheLink()
    {
        using var s = Show();
        var host = In<Volco.App.Views.Hardware.DeviceCard>(s.Window, "Card");
        Assert.StartsWith("Connected over USB", In<TextBlock>(host, "StatusText").Text);
        Assert.Equal(ThemeColor("SuccessBrush"), ((ISolidColorBrush)In<Avalonia.Controls.Shapes.Ellipse>(host, "StatusDot").Fill!).Color);

        s.Vm.Hardware.Card.DisconnectCommand.Execute(null);
        Until(s.Window, () => In<TextBlock>(host, "StatusText").Text == "Disconnected");
        var connect = In<Button>(host, "ActionButton");
        Assert.Equal("Connect", connect.Content);
        var old = s.Conn;
        Press(connect);
        Until(s.Window, () => In<TextBlock>(host, "StatusText").Text == "Looking for your controller");
        Assert.NotSame(old, s.Rig.Connection);
    }

    private static Color ThemeColor(string key)
    {
        var app = Application.Current!;
        Assert.True(app.TryGetResource(key, app.ActualThemeVariant, out var value));
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    // ── Leaving with unsaved changes (spec §4.4) ─────────────────────────────────────────────

    [AvaloniaFact]
    public void LeavingWithChangesPrompts()
    {
        using var s = Show();
        LeaveDirty(s);

        Assert.Equal("controller", s.NavTag);
        Assert.Same(s.Stage, s.Window.CurrentPage);
        Assert.True(In<Border>(s.Window, "LeaveOverlay").IsVisible);
        Assert.Equal("Unsaved changes", In<TextBlock>(s.Window, "LeaveTitle").Text);

        // Stay (and Escape) keep the draft and the page.
        Press(In<Button>(s.Window, "StayButton"));
        Settle(s.Window);
        Assert.False(In<Border>(s.Window, "LeaveOverlay").IsVisible);
        Assert.Equal("controller", s.NavTag);
        Assert.True(s.Vm.Controller.IsDirty);

        s.Window.ShowPage("general");
        Settle(s.Window);
        Assert.True(In<Border>(s.Window, "LeaveOverlay").IsVisible);
        s.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Settle(s.Window);
        Assert.Null(s.Vm.Hardware.LeavePrompt);
        Assert.Equal("controller", s.NavTag);
        Assert.Equal(39u, s.Vm.Controller.Draft.Sliders[1].Pin);
    }

    [AvaloniaFact]
    public void DiscardAndContinue()
    {
        using var s = Show();
        LeaveDirty(s, "general");
        Press(In<Button>(s.Window, "DiscardAndContinueButton"));
        Settle(s.Window);

        Assert.IsType<GeneralPage>(s.Window.CurrentPage);
        Assert.Equal("general", s.NavTag);
        Assert.False(s.Vm.Controller.IsDirty);
        Assert.False(AnyWrite(s));
    }

    [AvaloniaFact]
    public void SaveAndContinueWaitsForConfirmation()
    {
        using var s = Show();
        LeaveDirty(s);
        Press(In<Button>(s.Window, "SaveAndContinueButton"));
        Until(s.Window, () => AnyWrite(s));

        Assert.Equal("controller", s.NavTag);   // no navigation before the controller confirms
        Assert.True(In<TextBlock>(s.Window, "LeaveProgress").IsVisible);
        Assert.Equal("Saving to the controller…", In<TextBlock>(s.Window, "LeaveProgress").Text);

        Answer(s, ConfigResultCode.Ok);
        Until(s.Window, () => s.NavTag == "general");
        Assert.IsType<GeneralPage>(s.Window.CurrentPage);
        Assert.Null(s.Vm.Hardware.LeavePrompt);
        Until(s.Window, () => !s.Vm.Controller.IsDirty);
    }

    [AvaloniaFact]
    public void ANavClickWhileThePromptSavesKeepsThePromptAndItsSave()
    {
        using var s = Show();
        LeaveDirty(s);
        var prompt = s.Vm.Hardware.LeavePrompt;
        Press(In<Button>(s.Window, "SaveAndContinueButton"));
        Until(s.Window, () => AnyWrite(s));

        s.Window.ShowPage("general");   // another click while the save runs
        Settle(s.Window);
        Assert.Same(prompt, s.Vm.Hardware.LeavePrompt);
        Assert.True(prompt!.IsSaving);
        Assert.False(In<Button>(s.Window, "DiscardAndContinueButton").IsEffectivelyEnabled);
        Assert.Equal("controller", s.NavTag);

        Answer(s, ConfigResultCode.Ok);
        Until(s.Window, () => s.NavTag == "general");   // continues to the page chosen last
        Assert.IsType<GeneralPage>(s.Window.CurrentPage);
    }

    [AvaloniaFact]
    public void LeavingWhileTheBarsSaveRuns_ThePromptWaitsForThatSave()
    {
        using var s = Show();
        s.Vm.Controller.SetSliderPin(1, 39);
        Settle(s.Window);
        Press(In<Button>(In<SaveBar>(s.Stage, "SaveBarHost"), "SaveButton"));
        Until(s.Window, () => AnyWrite(s));

        s.Window.ShowPage("general");
        Settle(s.Window);
        var prompt = s.Vm.Hardware.LeavePrompt;
        Assert.NotNull(prompt);
        Assert.True(prompt!.IsSaving);
        Assert.Equal("Saving to the controller…", In<TextBlock>(s.Window, "LeaveProgress").Text);
        Assert.False(In<Button>(s.Window, "DiscardAndContinueButton").IsEffectivelyEnabled);
        Assert.False(In<Button>(s.Window, "SaveAndContinueButton").IsEffectivelyEnabled);
        Assert.Equal("controller", s.NavTag);

        Answer(s, ConfigResultCode.Ok);
        Until(s.Window, () => s.NavTag == "general");
        Assert.IsType<GeneralPage>(s.Window.CurrentPage);
        Assert.Null(s.Vm.Hardware.LeavePrompt);
        Assert.Equal(39u, s.Vm.Controller.Draft.Sliders[1].Pin);   // the save landed, nothing reverted
    }

    [AvaloniaFact]
    public void EscapeCancelsStartOver()
    {
        using var s = Show();
        s.Vm.Hardware.StartOver();
        Settle(s.Window);
        var back = In<Button>(s.Stage, "ChooserBack");
        Assert.True(back.IsVisible);
        back.Focus();
        s.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Settle(s.Window);
        Assert.False(s.Vm.Hardware.ShowChooser);
        Assert.Equal(5, s.Vm.Controller.Draft.Sliders.Count);
    }

    [AvaloniaFact]
    public void SaveFailureStays()
    {
        using var s = Show();
        LeaveDirty(s);
        Press(In<Button>(s.Window, "SaveAndContinueButton"));
        Answer(s, ConfigResultCode.StorageFailed);
        Until(s.Window, () => !s.Vm.Controller.IsSaving);

        Assert.Equal("controller", s.NavTag);
        Assert.Null(s.Vm.Hardware.LeavePrompt);
        var failure = "The controller couldn't save the setup. Its previous setup is unchanged.";
        Assert.Equal(failure, s.Vm.Controller.Failure);
        Assert.Equal(failure, In<TextBlock>(In<Volco.App.Views.Hardware.DeviceCard>(s.Window, "Card"), "DetailText").Text);
        Assert.True(s.Vm.Controller.IsDirty);
    }

    [AvaloniaFact]
    public void InvalidDraftOffersOnlyDiscardAndStay()
    {
        using var s = Show();
        s.Vm.Controller.SetSliderPin(0, 39);
        LeaveDirty(s);   // Slider 2 on 39 too: a conflict

        Assert.False(In<Button>(s.Window, "SaveAndContinueButton").IsEffectivelyEnabled);
        Assert.True(In<Button>(s.Window, "DiscardAndContinueButton").IsEffectivelyEnabled);
        Assert.True(In<Button>(s.Window, "StayButton").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void HidingKeepsTheDraft()
    {
        using var s = Show();
        s.Vm.Controller.SetSliderPin(1, 39);
        s.Window.Close();   // hides, without asking
        Settle(s.Window);
        Assert.False(s.Window.IsVisible);
        Assert.Null(s.Vm.Hardware.LeavePrompt);
        Assert.True(s.Vm.Controller.IsDirty);

        s.Window.Show();
        Settle(s.Window);
        Assert.Equal("controller", s.NavTag);
        Assert.Equal(39u, s.Vm.Controller.Draft.Sliders[1].Pin);
        Assert.True(In<SaveBar>(s.Stage, "SaveBarHost").IsVisible);
    }
}
