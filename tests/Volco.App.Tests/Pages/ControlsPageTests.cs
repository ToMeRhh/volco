using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Controls.Shapes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.Controls;
using Volco.App.ViewModels;
using Volco.App.Views;
using Volco.App.Views.Pages;
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Input;
using Volco.Core.Input.Actions;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.App.Tests.Pages;

/// <summary>Spec docs/design/controls-page.md §3–§5: the Controls page's navigation, window,
/// drawing and inspector.</summary>
public class ControlsPageTests
{
    private static FakeAudioEngine EngineWithAudio()
    {
        var speakers = new AudioDevice("dev-1", "Speakers", AudioFlow.Render, true);
        var engine = new FakeAudioEngine
        {
            Devices = { speakers, new AudioDevice("dev-2", "Headphones", AudioFlow.Render, false) },
            DefaultRender = speakers,
        };
        engine.DeviceStates["dev-1"] = (0.5f, false);
        return engine;
    }

    private static void Hardware(AppConfig c, int sliders, params IndicatorKind[] buttons)
        => c.Controller = new ControllerInfoConfig { ProtocolVersion = 1, Sliders = sliders, Buttons = buttons.ToList() };

    private sealed record Shown(ControlsPage Page, Window Window, TestRig Rig, SettingsViewModel Vm) : IDisposable
    {
        public ControlsViewModel Controls => Vm.Controls;
        public AppConfig Config => Rig.Config.Config;

        public void Dispose()
        {
            Vm.Dispose();
            Rig.Dispose();
        }
    }

    /// <summary>The page in a window the size of the default window's page area.</summary>
    private static Shown Show(Action<AppConfig>? configure = null)
    {
        var rig = new TestRig(EngineWithAudio());
        configure?.Invoke(rig.Config.Config);
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new ControlsPage(vm.Controls);
        var window = new Window { Content = page, Width = 932, Height = 680 };
        window.Show();
        Settle(window);
        return new Shown(page, window, rig, vm);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static SettingsWindow OpenWindow(TestRig rig, SettingsViewModel vm, double width)
    {
        var window = new SettingsWindow(vm, rig.Runtime) { Width = width, Height = 720, AllowClose = true };
        window.Show();
        Settle(window);
        return window;
    }

    private static List<T> All<T>(Visual root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().Where(c => c.Name == name).ToList();

    private static T In<T>(Visual root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static List<T> Tiles<T>(Visual root) where T : Control
        => root.GetVisualDescendants().OfType<T>().ToList();

    private static Border Inspector(ControlsPage page) => page.FindControl<Border>("Inspector")!;

    /// <summary>A control in one of the inspector's action rows ("Press", "Double", "Hold").</summary>
    private static T InRow<T>(ControlsPage page, string row, string name) where T : Control
        => In<T>(In<ContentControl>(Inspector(page), $"ActionRow_{row}"), name);

    private static double TopIn(Visual part, Visual tile) => part.TranslatePoint(new Point(0, 0), tile)!.Value.Y;

    // ── Navigation and window ─────────────────────────────────────────────────

    [AvaloniaFact]
    public void Nav_HasGeneralControlsHardware()
    {
        using var rig = new TestRig(EngineWithAudio());
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var window = OpenWindow(rig, vm, 1100);
        var nav = window.FindControl<ListBox>("NavList")!;

        var items = nav.Items.Cast<NavItem>().ToList();
        Assert.Equal(new[] { "General", "Controls", "Hardware" }, items.Select(n => n.Label));
        Assert.Equal(new[] { "general", "controls", "controller" }, items.Select(n => n.Tag));
        Assert.True(Application.Current!.TryFindResource("Icon.Sliders", out var sliders));
        Assert.Same(sliders, items[1].Icon);

        window.ShowPage("controls");
        Assert.IsType<ControlsPage>(window.CurrentPage);
        window.Close();
    }

    [AvaloniaFact]
    public void Window_DefaultSize_1100x720_Min_820x520()
    {
        using var rig = new TestRig(EngineWithAudio());
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var window = new SettingsWindow(vm, rig.Runtime);

        Assert.Equal(1100, window.Width);
        Assert.Equal(720, window.Height);
        Assert.Equal(820, window.MinWidth);
        Assert.Equal(520, window.MinHeight);
    }

    [AvaloniaFact]
    public void Compact_Below1000_SidebarIcons_Inspector300()
    {
        using var rig = new TestRig(EngineWithAudio());
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());

        var wide = OpenWindow(rig, vm, 1100);
        wide.ShowPage("controls");
        Settle(wide);
        Assert.False(wide.Classes.Contains("compact"));
        Assert.Equal(168, wide.FindControl<Border>("Sidebar")!.Bounds.Width);
        Assert.All(Tiles<TextBlock>(wide).Where(t => t.Classes.Contains("NavLabel")), t => Assert.True(t.IsEffectivelyVisible));
        Assert.Equal(380, Inspector((ControlsPage)wide.CurrentPage!).Bounds.Width);
        wide.Close();

        using var vm2 = rig.CreateSettings(new AvaloniaUiDispatcher());
        var narrow = OpenWindow(rig, vm2, 900);
        narrow.ShowPage("controls");
        Settle(narrow);
        Assert.True(narrow.Classes.Contains("compact"));
        Assert.Equal(56, narrow.FindControl<Border>("Sidebar")!.Bounds.Width);
        var labels = Tiles<TextBlock>(narrow).Where(t => t.Classes.Contains("NavLabel")).ToList();
        Assert.Equal(3, labels.Count);
        Assert.All(labels, t => Assert.False(t.IsEffectivelyVisible));
        var page = (ControlsPage)narrow.CurrentPage!;
        Assert.Equal(300, Inspector(page).Bounds.Width);
        // The drawing still sits beside the inspector.
        var drawing = page.FindControl<ScrollViewer>("DrawingScroll")!;
        Assert.True(drawing.Bounds.Width > 300, $"drawing is {drawing.Bounds.Width} wide");
        narrow.Close();
    }

    // ── The drawing ───────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Drawing_FiveSlidersThreeButtons_FromHello()
    {
        using (var before = Show())
            Assert.True(before.Page.FindControl<TextBlock>("ConnectLineText")!.IsEffectivelyVisible);

        using var s = Show(c => Hardware(c, 5, IndicatorKind.Led, IndicatorKind.LedPair, IndicatorKind.Screen));

        Assert.IsType<FillWrapPanel>(In<FillWrapPanel>(s.Page, "SliderGrid"));
        Assert.IsType<FillWrapPanel>(In<FillWrapPanel>(s.Page, "ButtonGrid"));
        Assert.Equal(5, Tiles<SliderTile>(s.Page).Count);
        var buttons = Tiles<ButtonTile>(s.Page);
        Assert.Equal(3, buttons.Count);
        Assert.False(s.Page.FindControl<TextBlock>("ConnectLineText")!.IsEffectivelyVisible);

        Assert.True(In<Ellipse>(buttons[0], "Led").IsEffectivelyVisible);
        Assert.True(In<Ellipse>(buttons[1], "LedA").IsEffectivelyVisible);
        Assert.True(In<ScreenPreview>(buttons[2], "ScreenPreviewTile").IsEffectivelyVisible);
        Assert.False(In<ScreenPreview>(buttons[0], "ScreenPreviewTile").IsEffectivelyVisible);
        foreach (var tile in buttons)
            foreach (var part in new[] { "Indicator", "Cap", "MainAction", "SecondaryActions", "Grip" })
                Assert.NotEmpty(All<Control>(tile, part));
        Assert.All(buttons, b => Assert.Equal("Not set", In<TextBlock>(b, "MainAction").Text));
        Assert.Equal("Not set", In<TextBlock>(Tiles<SliderTile>(s.Page)[0], "Label").Text);
    }

    [AvaloniaFact]
    public void ButtonTiles_PartsAlign()
    {
        using var s = Show(c =>
        {
            Hardware(c, 5, IndicatorKind.Led, IndicatorKind.Screen, IndicatorKind.Led);
            c.Buttons[0].Default().SinglePress.Action = ButtonActionType.MuteCurrentOutput;
            c.Buttons[0].Default().LongPress.Action = ButtonActionType.MuteCurrentInput;
            c.Buttons[1].Default().SinglePress.Action = ButtonActionType.NextPreset;
        });
        var tiles = Tiles<ButtonTile>(s.Page);
        Assert.Single(s.Vm.Buttons.Items[0].Secondary);
        // The screen is taller than an LED, so the rows below it would drift without sharing.
        Assert.True(In<Panel>(tiles[1], "Indicator").Bounds.Height > In<Ellipse>(tiles[0], "Led").Bounds.Height + 10);

        foreach (var part in new[] { "Cap", "MainAction", "SecondaryActions" })
        {
            var tops = tiles.Select(t => TopIn(In<Control>(t, part), t)).ToList();
            Assert.All(tops, top => Assert.Equal(tops[0], top, 1));
        }
    }

    [AvaloniaFact]
    public void Dense_TwelveSliders_IconsRow()
    {
        using var s = Show(c => Hardware(c, 12, IndicatorKind.Led, IndicatorKind.Screen, IndicatorKind.Led));

        Assert.True(s.Controls.IsDense);
        Assert.Equal(12, Tiles<SliderTile>(s.Page).Count);
        Assert.Equal(84, In<Panel>(Tiles<SliderTile>(s.Page)[0], "Fader").Bounds.Height);
        foreach (var tile in Tiles<ButtonTile>(s.Page))
        {
            Assert.True(In<StackPanel>(tile, "ActionIcons").IsEffectivelyVisible);
            Assert.False(In<TextBlock>(tile, "MainAction").IsEffectivelyVisible);
            Assert.False(In<ItemsControl>(tile, "SecondaryActions").IsEffectivelyVisible);
        }
        Assert.False(In<ScreenPreview>(Tiles<ButtonTile>(s.Page)[1], "ScreenPreviewTile").IsEffectivelyVisible);   // text only
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void ClickTile_SelectsAndShowsInspector()
    {
        using var s = Show(c => Hardware(c, 5, IndicatorKind.Led, IndicatorKind.LedPair, IndicatorKind.Screen));
        Assert.Same(s.Vm.Sliders.Items[0], s.Controls.Selected);
        Assert.Equal("Slider 1", In<TextBlock>(Inspector(s.Page), "HeadingText").Text);

        var tile = Tiles<ButtonTile>(s.Page)[1];
        var point = tile.TranslatePoint(new Point(tile.Bounds.Width / 2, 6), s.Window)!.Value;
        s.Window.MouseDown(point, MouseButton.Left);
        s.Window.MouseUp(point, MouseButton.Left);
        Settle(s.Window);

        Assert.Same(s.Vm.Buttons.Items[1], s.Controls.Selected);
        Assert.True(s.Vm.Buttons.Items[1].IsSelected);
        Assert.False(s.Vm.Sliders.Items[0].IsSelected);
        Assert.Equal("Button 2", In<TextBlock>(Inspector(s.Page), "HeadingText").Text);
        Assert.Equal("LED pair", In<TextBlock>(Inspector(s.Page), "KindText").Text);
        Assert.Equal(new[] { "Press", "Double press", "Hold" },
            new[] { "Press", "Double", "Hold" }.Select(r => InRow<TextBlock>(s.Page, r, "GestureText").Text));
        Assert.Equal("—", InRow<TextBlock>(s.Page, "Press", "ActionText").Text);
        Assert.Equal("Off", In<TextBlock>(Inspector(s.Page), "LightText").Text);
        Assert.Equal("Light", In<TextBlock>(Inspector(s.Page), "LightLabel").Text);
        var cfg = s.Config.Buttons[1];
        Assert.Equal($"{cfg.LongPressMs} ms · {cfg.DoublePressWindowMs} ms", In<TextBlock>(Inspector(s.Page), "TimingSummaryText").Text);
        Assert.False(In<StackPanel>(Inspector(s.Page), "TimingBody").IsEffectivelyVisible);   // starts closed
    }

    [AvaloniaFact]
    public void ArrowKeys_MoveFocus()
    {
        using var s = Show();
        var tiles = s.Page.Tiles();
        Assert.Equal(8, tiles.Count);   // 5 sliders, 3 buttons
        tiles[0].Focus();

        s.Window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.True(tiles[1].IsFocused);
        s.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Assert.True(tiles[2].IsFocused);
        s.Window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        Assert.True(tiles[1].IsFocused);
        Assert.Same(s.Vm.Sliders.Items[0], s.Controls.Selected);   // focus alone doesn't select

        s.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Same(s.Vm.Sliders.Items[1], s.Controls.Selected);

        // From the last slider, Right goes on to the first button.
        tiles[4].Focus();
        s.Window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.True(tiles[5].IsFocused);
        Assert.IsType<ButtonTile>(tiles[5]);
    }

    // ── Slider inspector ──────────────────────────────────────────────────────

    [AvaloniaFact]
    public void SliderName_Typed_ThenHardwarePress_NameSaved()
    {
        using var s = Show();
        var box = In<TextBox>(Inspector(s.Page), "NameBox");
        Assert.Equal("Not set", box.PlaceholderText);
        box.Focus();
        s.Window.KeyTextInput("Desk");
        Dispatcher.UIThread.RunJobs();

        // A press on the controller selects button 2 while the name box still has focus.
        s.Controls.OnButtonActivity(1);
        Settle(s.Window);

        Assert.Same(s.Vm.Buttons.Items[1], s.Controls.Selected);
        Assert.Equal("Desk", s.Config.Sliders[0].Name);
        Assert.Equal("Desk", s.Vm.Sliders.Items[0].Label);
        Assert.Equal("Desk", In<TextBlock>(Tiles<SliderTile>(s.Page)[0], "Label").Text);

        s.Controls.Select(s.Vm.Sliders.Items[0]);
        Settle(s.Window);
        Assert.Equal("Desk", In<TextBox>(Inspector(s.Page), "NameBox").Text);
    }

    // ── Button inspector ──────────────────────────────────────────────────────

    [AvaloniaFact]
    public void ActionRow_Click_OpensDialog()
    {
        using var s = Show();
        var button = s.Vm.Buttons.Items[0];
        s.Controls.Select(button);
        Settle(s.Window);
        var overlay = s.Page.FindControl<Border>("DialogOverlay")!;
        Assert.False(overlay.IsVisible);

        var row = InRow<Button>(s.Page, "Double", "RowButton");
        row.Command!.Execute(row.CommandParameter);
        Settle(s.Window);

        Assert.True(s.Controls.HasDialog);
        var request = Assert.IsType<PendingDialogRequest>(s.Controls.Dialog);
        Assert.Same(button, request.Control);
        Assert.Equal(PressType.Double, request.Gesture);
        Assert.True(overlay.IsVisible);

        s.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(s.Controls.HasDialog);

        var controls = In<Button>(s.Page, "RowButton");   // the Press row
        controls.Command!.Execute(controls.CommandParameter);
        Assert.Equal(PressType.Single, Assert.IsType<PendingDialogRequest>(s.Controls.Dialog).Gesture);
        s.Page.FindControl<Button>("CloseDialogButton")!.Command!.Execute(null);
        Assert.False(s.Controls.HasDialog);

        // The slider's Controls row opens the target dialog.
        s.Controls.Select(s.Vm.Sliders.Items[2]);
        Settle(s.Window);
        var target = In<Button>(Inspector(s.Page), "ControlsRow");
        target.Command!.Execute(target.CommandParameter);
        Assert.Same(s.Vm.Sliders.Items[2], Assert.IsType<PendingDialogRequest>(s.Controls.Dialog).Control);
        Assert.Null(((PendingDialogRequest)s.Controls.Dialog!).Gesture);
    }

    [AvaloniaFact]
    public void Try_RunsWithOrigin()
    {
        using var rig = new TestRig(EngineWithAudio());
        rig.Config.Config.Buttons[0].Default().SinglePress.Action = ButtonActionType.MuteCurrentOutput;
        var toast = new UndoToastViewModel();
        var runs = new List<(ButtonActionConfig Action, ActionOrigin? Origin)>();
        var sliders = new SlidersViewModel(rig.Config, rig.Engine, toast);
        var buttons = new ButtonsViewModel(rig.Config, rig.Engine, rig.Runtime, (a, o) => runs.Add((a, o)), toast);
        var controls = new ControlsViewModel(sliders, buttons, rig.Config, toast);
        var page = new ControlsPage(controls);
        var window = new Window { Content = page, Width = 932, Height = 680 };
        window.Show();
        controls.Select(buttons.Items[0]);
        Settle(window);

        var tryButton = InRow<Button>(page, "Press", "TryButton");
        Assert.True(tryButton.IsEffectivelyVisible);
        Assert.False(InRow<Button>(page, "Double", "TryButton").IsEffectivelyVisible);   // empty: a + instead
        Assert.True(InRow<PathIcon>(page, "Double", "AddIcon").IsEffectivelyVisible);
        tryButton.Command!.Execute(null);

        var run = Assert.Single(runs);
        Assert.Same(rig.Config.Config.Buttons[0].Default().SinglePress, run.Action);
        Assert.Equal(new ActionOrigin(0, PressType.Single, rig.Config.Config.Presets[0].Name), run.Origin);
    }

    [AvaloniaFact]
    public void Clear_DisabledWhenEmpty_UndoRestores()
    {
        using var s = Show(c => c.Buttons[0].Default().SinglePress.Action = ButtonActionType.MuteCurrentOutput);
        s.Controls.Select(s.Vm.Buttons.Items[1]);
        Settle(s.Window);
        Assert.False(In<Button>(Inspector(s.Page), "ClearButton").IsEffectivelyEnabled);

        s.Controls.Select(s.Vm.Buttons.Items[0]);
        Settle(s.Window);
        var clear = In<Button>(Inspector(s.Page), "ClearButton");
        Assert.True(clear.IsEffectivelyEnabled);
        clear.Command!.Execute(clear.CommandParameter);
        Settle(s.Window);

        Assert.Equal(ButtonActionType.None, s.Config.Buttons[0].Default().SinglePress.Action);
        Assert.False(clear.IsEffectivelyEnabled);
        Assert.Equal("—", InRow<TextBlock>(s.Page, "Press", "ActionText").Text);
        Assert.True(s.Vm.Toast.IsOpen);

        s.Vm.Toast.UndoCommand.Execute(null);
        Settle(s.Window);
        Assert.Equal(ButtonActionType.MuteCurrentOutput, s.Config.Buttons[0].Default().SinglePress.Action);
        Assert.True(clear.IsEffectivelyEnabled);
        Assert.Equal("Mute output", InRow<TextBlock>(s.Page, "Press", "ActionText").Text);
    }

    [AvaloniaFact]
    public void MissingControl_RemoveRemovesLast_WithUndo()
    {
        using var s = Show(c => Hardware(c, 3, IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.Led));
        var sliderTiles = Tiles<SliderTile>(s.Page);
        Assert.True(In<TextBlock>(sliderTiles[3], "MissingText").IsEffectivelyVisible);
        Assert.False(In<TextBlock>(sliderTiles[2], "MissingText").IsEffectivelyVisible);

        // Slider 4 is missing but not last: no Remove (input number is position).
        s.Controls.Select(s.Vm.Sliders.Items[3]);
        Settle(s.Window);
        Assert.True(In<Border>(Inspector(s.Page), "MissingPanel").IsEffectivelyVisible);
        Assert.False(In<Button>(Inspector(s.Page), "RemoveButton").IsEffectivelyVisible);

        s.Controls.Select(s.Vm.Sliders.Items[4]);
        Settle(s.Window);
        var remove = In<Button>(Inspector(s.Page), "RemoveButton");
        Assert.True(remove.IsEffectivelyVisible);
        remove.Command!.Execute(null);
        Settle(s.Window);

        Assert.Equal(4, s.Config.Sliders.Count);
        Assert.Equal(4, Tiles<SliderTile>(s.Page).Count);
        Assert.Same(s.Vm.Sliders.Items[0], s.Controls.Selected);
        Assert.True(s.Vm.Toast.IsOpen);

        s.Vm.Toast.UndoCommand.Execute(null);
        Settle(s.Window);
        Assert.Equal(5, s.Config.Sliders.Count);
        Assert.Equal(5, Tiles<SliderTile>(s.Page).Count);
    }

    [AvaloniaFact]
    public void Inspector_HeightDoesNotChange_WhenActionsChange()
    {
        using var s = Show(c => Hardware(c, 5, IndicatorKind.Screen, IndicatorKind.Led, IndicatorKind.Led));
        var button = s.Vm.Buttons.Items[0];
        s.Controls.Select(button);
        Settle(s.Window);
        var before = Inspector(s.Page).Bounds.Height;
        Assert.True(before > 0);

        button.Press.SelectedChoice = ActionCatalog.For(ButtonActionType.MuteCurrentOutput);
        button.DoublePress.SelectedChoice = ActionCatalog.For(ButtonActionType.NextPreset);
        Settle(s.Window);

        Assert.Equal("Mute output", InRow<TextBlock>(s.Page, "Press", "ActionText").Text);
        Assert.Equal("Follows Press", In<TextBlock>(Inspector(s.Page), "LightText").Text);
        Assert.Equal("Screen", In<TextBlock>(Inspector(s.Page), "LightLabel").Text);
        Assert.Equal(before, Inspector(s.Page).Bounds.Height);
    }

    [AvaloniaFact]
    public void SavedInvalidAction_RowShowsWarning()
    {
        using var s = Show(c => c.Buttons[0].Default().SinglePress = new ButtonActionConfig
        {
            Action = ButtonActionType.RunScript, Command = new CommandConfig { Path = "" },
        });
        s.Controls.Select(s.Vm.Buttons.Items[0]);
        Settle(s.Window);

        var icon = InRow<PathIcon>(s.Page, "Press", "InvalidIcon");
        Assert.True(icon.IsEffectivelyVisible);
        Assert.Equal("Choose the script to run.", ToolTip.GetTip(icon));
        Assert.False(InRow<PathIcon>(s.Page, "Double", "InvalidIcon").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void ProgramAction_ShowsUsbTag()
    {
        using var s = Show(c => c.Buttons[0].Default().SinglePress = new ButtonActionConfig
        {
            Action = ButtonActionType.OpenProgram, Command = new CommandConfig { Path = @"C:\Games\game.exe" },
        });
        s.Controls.Select(s.Vm.Buttons.Items[0]);
        Settle(s.Window);

        var tag = InRow<Border>(s.Page, "Press", "UsbTag");
        Assert.True(tag.IsEffectivelyVisible);
        Assert.Equal("Runs only for presses over the USB cable", ToolTip.GetTip(tag));
        Assert.False(InRow<Border>(s.Page, "Double", "UsbTag").IsEffectivelyVisible);
    }

    // ── Routing from the runtime (Task 6 review) ──────────────────────────────

    [Fact]
    public void SettingsViewModel_ButtonActivitySelects_ReconnectStartsTheHelloWindow()
    {
        using var rig = new TestRig(EngineWithAudio());
        var ui = new RecordingDispatcher();
        using var vm = rig.CreateSettings(ui);
        var controls = vm.Controls;
        var hello = new HelloMessage(ProtocolVersion.Current, 5, new[] { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.Led });
        rig.Connection!.SetStatus(TransportStatus.Open);
        rig.Connection.RaiseMessage(hello);
        ui.RunAll();

        rig.Connection.RaiseMessage(new ButtonMessage(1, ButtonState.Pressed));
        rig.Connection.RaiseMessage(new ButtonMessage(1, ButtonState.Released));
        ui.RunAll();
        Assert.Same(vm.Buttons.Items[1], controls.Selected);

        // Past the first Hello's window, the link drops and comes back with the same controller:
        // only the link's change to Connected tells the page about this Hello.
        Thread.Sleep(1100);
        rig.Connection.SetStatus(TransportStatus.Closed);
        ui.RunAll();
        controls.OnSliderLevel(2, 0.20f);   // the slider's first report: its baseline
        rig.Connection.SetStatus(TransportStatus.Open);
        rig.Connection.RaiseMessage(hello);
        ui.RunAll();

        controls.OnSliderLevel(2, 0.90f);   // the Hello's sync, not a move
        Assert.Same(vm.Buttons.Items[1], controls.Selected);
    }
}
