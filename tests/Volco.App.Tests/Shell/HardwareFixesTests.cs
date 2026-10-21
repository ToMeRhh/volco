using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.Controls;
using Volco.App.ViewModels;
using Volco.App.ViewModels.Hardware;
using Volco.App.Views;
using Volco.App.Views.Hardware;
using Volco.App.Views.Inspector;
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
/// Regressions from the Hardware page's Windows test (2026-10-21), each through the real settings
/// window: the title row during a test (spec §8), a removed control leaving Volco's settings and
/// the Controls page (§6.5), one fader that never fades (§3.3), and the side panel after General
/// (§3.3) with the crossfades on.
/// </summary>
public sealed class HardwareFixesTests : IDisposable
{
    private static readonly ControllerPreset Diy = ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId);
    private static readonly IndicatorKind[] DiyKinds = { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair };

    public HardwareFixesTests() => StageTransitions.Enabled = false;

    public void Dispose() => StageTransitions.Enabled = false;

    private sealed record Shown(SettingsWindow Window, TestRig Rig, SettingsViewModel Vm) : IDisposable
    {
        public StagePage Stage => Assert.IsType<StagePage>(Window.CurrentPage);
        public FakeControllerConnection Conn => Rig.Connection!;
        public AppConfig Config => Rig.Config.Config;

        public void Dispose()
        {
            Window.AllowClose = true;
            Window.Close();
            Vm.Dispose();
            Rig.Dispose();
        }
    }

    /// <summary>The settings window, opened on General as the app opens it, then on Hardware over
    /// the DIY setup (5 sliders named a..e; buttons LED, LED, pair with hold times 501..503), in
    /// sync with the controller.</summary>
    private static Shown Show()
    {
        var blob = ControllerConfigCodec.Encode(ControllerPresets.ApplyTo(new ControllerConfig(), Diy));
        var rig = new TestRig();
        var c = rig.Config.Config;
        c.Sliders = "abcde".Select((n, i) => new SliderConfig { Index = i, Name = n.ToString(), Type = SliderMappingType.None }).ToList();
        c.Buttons = Enumerable.Range(0, 3).Select(i => new ButtonConfig { Index = i, LongPressMs = 501 + i }).ToList();
        c.ControllerConfigBlob = Convert.ToBase64String(blob);
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var window = new SettingsWindow(vm, rig.Runtime) { Width = 1100, Height = 720 };
        window.Show();
        rig.Connection!.SetStatus(TransportStatus.Open);
        rig.Connection.RaiseMessage(Hello(ControllerConfigCodec.Crc32(blob), 5, DiyKinds));
        Pump(window, 0);
        window.ShowPage("controller");
        Until(window, () => vm.Controller.CanEdit && vm.Hardware.CanStartTest);
        Pump(window);
        return new Shown(window, rig, vm);
    }

    private static HelloMessage Hello(uint crc, int sliders, IndicatorKind[] kinds)
        => new(ProtocolVersion.Current, ChipFamily.Esp32, "dev", ControllerConfigState.Stored, crc, sliders, kinds);

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
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

    /// <summary>Runs the UI (and its animations, on the render timer) for <paramref name="ms"/>:
    /// long enough for a 150 ms crossfade to end.</summary>
    private static void Pump(Window window, int ms = 400)
    {
        var sw = Stopwatch.StartNew();
        do
        {
            Settle(window);
            Thread.Sleep(10);
        } while (sw.ElapsedMilliseconds < ms);
        Settle(window);
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
        Assert.True(button.IsEffectivelyEnabled, $"{button.Name} is enabled");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (button.Command is { } command && command.CanExecute(button.CommandParameter)) command.Execute(button.CommandParameter);
    }

    /// <summary>The opacity <paramref name="visual"/> is drawn with: its own times every ancestor's.</summary>
    private static double DrawnOpacity(Visual visual)
    {
        var opacity = 1.0;
        for (Visual? v = visual; v is not null; v = v.GetVisualParent()) opacity *= v.Opacity;
        return opacity;
    }

    /// <summary><paramref name="control"/> is on screen: visible and fully opaque.</summary>
    private static void AssertDrawn(Control control)
    {
        Assert.True(control.IsEffectivelyVisible, $"{control.GetType().Name} is visible");
        Assert.Equal(1.0, DrawnOpacity(control), 3);
    }

    // ── 1. During a test only Test is active in the title row (spec §8) ──────────────────────

    [AvaloniaFact]
    public void DuringATestTheTitleRowOffersOnlyStopTest()
    {
        using var s = Show();
        var actions = In<TransitioningContentControl>(s.Stage, "TitleActions");
        var chip = In<Button>(actions, "ControllerSettingsChip");
        var add = In<Button>(actions, "AddButton");
        Assert.True(chip.IsEffectivelyEnabled);
        Assert.True(add.IsEffectivelyEnabled);

        s.Vm.Controls.Select(s.Vm.Sliders.Items[1]);
        Press(In<Button>(actions, "TestButton"));
        Until(s.Window, () => s.Rig.Runtime.InTestMode && s.Vm.Hardware.IsTesting);

        Assert.False(chip.IsEffectivelyEnabled);       // the inspector is hidden: the chip would do nothing
        Assert.False(add.IsEffectivelyEnabled);        // editing is paused
        Assert.True(In<Button>(actions, "StopTestButton").IsEffectivelyEnabled);
        s.Vm.Hardware.SelectControllerSettings();      // nor does the view model take it
        Assert.IsNotType<ControllerSettingsViewModel>(s.Vm.Hardware.Inspected);

        Press(In<Button>(actions, "StopTestButton"));
        Until(s.Window, () => !s.Rig.Runtime.InTestMode && !s.Vm.Hardware.IsTesting);
        Assert.True(chip.IsEffectivelyEnabled);
        Click(s, chip);
        Assert.IsType<ControllerSettingsViewModel>(s.Vm.Hardware.Inspected);
    }

    // ── 2. A removed control leaves Volco's settings and the Controls page (spec §6.5) ───────

    /// <summary>How the controller answers the write.</summary>
    public enum Reply
    {
        /// <summary>ConfigResult OK, then the reboot's Hello.</summary>
        OkThenHello,

        /// <summary>ConfigResult OK, the link drops while it reboots, then its Hello.</summary>
        OkThenReconnect,

        /// <summary>No ConfigResult (lost), only the reboot's Hello: the implied OK.</summary>
        ImpliedOk,
    }

    /// <summary>Saves the draft with the save bar and answers as <paramref name="reply"/> says,
    /// with a Hello of the saved setup and its counts.</summary>
    private static void SaveAndAnswer(Shown s, Reply reply)
    {
        var draft = s.Vm.Controller.Draft;
        var blob = ControllerConfigCodec.Encode(draft);
        var kinds = draft.Buttons.Select(b => b.IndicatorCase switch
        {
            Volco.Core.Controller.Proto.Button.IndicatorOneofCase.Led => IndicatorKind.Led,
            Volco.Core.Controller.Proto.Button.IndicatorOneofCase.LedPair => IndicatorKind.LedPair,
            Volco.Core.Controller.Proto.Button.IndicatorOneofCase.Screen => IndicatorKind.Screen,
            _ => IndicatorKind.None,
        }).ToArray();
        Press(In<Button>(In<SaveBar>(s.Stage, "SaveBarHost"), "SaveButton"));
        Until(s.Window, () => s.Conn.SentFrames.Any(f => f.Length > 3 && f[2] == 0x88));
        var burst = s.Conn.SentFrames.Last(f => f.Length > 3 && f[2] == 0x88);
        if (reply != Reply.ImpliedOk)
            s.Conn.RaiseMessage(new ConfigResultMessage(burst[3], ConfigResultCode.Ok, 0xFF, 0xFF));
        Settle(s.Window);
        if (reply == Reply.OkThenReconnect)
        {
            s.Conn.SetStatus(TransportStatus.Closed);
            Pump(s.Window, 50);
            s.Conn.SetStatus(TransportStatus.Open);
            Settle(s.Window);
        }
        s.Conn.RaiseMessage(Hello(ControllerConfigCodec.Crc32(blob), draft.Sliders.Count, kinds));
        for (int i = 0; i < draft.Sliders.Count; i++) s.Conn.RaiseMessage(new SliderMessage(i, 2048));   // the boot's positions
        Until(s.Window, () => !s.Vm.Controller.IsSaving);
        Pump(s.Window, 50);
        Assert.Equal("Saved. The controller restarted with the new setup.", s.Vm.Controller.ProgressText);
    }

    /// <summary>Volco's settings as config.json has them once written.</summary>
    private static AppConfig Written(Shown s)
    {
        s.Rig.Config.FlushPendingSave();
        var copy = new ConfigManager(s.Rig.Config.ConfigPath);
        copy.Load();
        return copy.Config;
    }

    /// <summary>Selects tile <paramref name="index"/> of kind <typeparamref name="TTile"/> with a
    /// click, then Remove and the confirmation in the inspector.</summary>
    private static void RemoveWithTheInspector<TTile>(Shown s, int index) where TTile : Control
    {
        Click(s, s.Stage.Tiles().OfType<TTile>().ElementAt(index));
        var host = (HardwareInspectorHost)s.Stage.HardwareSidePanel!;
        Press(In<Button>(host, "RemoveButton"));
        Settle(s.Window);
        Press(In<Button>(host, "ConfirmButton"));
        Settle(s.Window);
    }

    private static void ShowControls(Shown s)
    {
        s.Window.ShowPage("controls");
        Pump(s.Window, 50);
        Assert.Equal(StageMode.Controls, s.Stage.Mode);
        Assert.Null(s.Vm.Hardware.LeavePrompt);   // nothing unsaved
    }

    private static string[] TileNames(Shown s)
        => s.Stage.Tiles().OfType<SliderTile>().Select(t => ((SliderItemViewModel)t.DataContext!).Config.Name).ToArray();

    [AvaloniaTheory]
    [InlineData(Reply.OkThenHello)]
    [InlineData(Reply.OkThenReconnect)]
    [InlineData(Reply.ImpliedOk)]
    public void RemovingAMiddleSliderLeavesTheSettingsAndControls(Reply reply)
    {
        using var s = Show();
        RemoveWithTheInspector<SliderTile>(s, 2);
        Assert.Equal(4, s.Vm.Controller.Draft.Sliders.Count);
        SaveAndAnswer(s, reply);
        ShowControls(s);

        Assert.Equal(new[] { "a", "b", "d", "e" }, s.Config.Sliders.Select(x => x.Name));
        Assert.Equal(new[] { 0, 1, 2, 3 }, s.Config.Sliders.Select(x => x.Index));
        Assert.Equal(new[] { "a", "b", "d", "e" }, Written(s).Sliders.Select(x => x.Name));
        Assert.Equal(new[] { "a", "b", "d", "e" }, s.Vm.Sliders.Items.Select(x => x.Config.Name));
        Assert.Equal(new[] { "a", "b", "d", "e" }, TileNames(s));
        Assert.DoesNotContain(s.Vm.Sliders.Items, x => x.IsMissing);
        Assert.Equal(new[] { "Slider 1", "Slider 2", "Slider 3", "Slider 4" }, s.Vm.Sliders.Items.Select(x => x.Heading));
    }

    [AvaloniaTheory]
    [InlineData(Reply.OkThenHello)]
    [InlineData(Reply.OkThenReconnect)]
    public void RemovingTheLastSliderLeavesTheSettingsAndControls(Reply reply)
    {
        using var s = Show();
        RemoveWithTheInspector<SliderTile>(s, 4);
        SaveAndAnswer(s, reply);
        ShowControls(s);

        Assert.Equal(new[] { "a", "b", "c", "d" }, s.Config.Sliders.Select(x => x.Name));
        Assert.Equal(new[] { "a", "b", "c", "d" }, Written(s).Sliders.Select(x => x.Name));
        Assert.Equal(new[] { "a", "b", "c", "d" }, TileNames(s));
        Assert.DoesNotContain(s.Vm.Sliders.Items, x => x.IsMissing);
    }

    [AvaloniaTheory]
    [InlineData(Reply.OkThenHello)]
    [InlineData(Reply.OkThenReconnect)]
    [InlineData(Reply.ImpliedOk)]
    public void RemovingAButtonLeavesTheSettingsAndControls(Reply reply)
    {
        using var s = Show();
        RemoveWithTheInspector<ButtonTile>(s, 0);
        SaveAndAnswer(s, reply);
        ShowControls(s);

        Assert.Equal(new[] { 502, 503 }, s.Config.Buttons.Select(x => x.LongPressMs));
        Assert.Equal(new[] { 0, 1 }, s.Config.Buttons.Select(x => x.Index));
        Assert.Equal(new[] { 502, 503 }, Written(s).Buttons.Select(x => x.LongPressMs));
        var tiles = s.Stage.Tiles().OfType<ButtonTile>().ToList();
        Assert.Equal(new[] { 502, 503 }, tiles.Select(t => ((ButtonItemViewModel)t.DataContext!).Config.LongPressMs));
        Assert.DoesNotContain(s.Vm.Buttons.Items, x => x.IsMissing);
    }

    // ── 3. One fader per slider tile, never faded (spec §3.3) ────────────────────────────────

    [AvaloniaFact]
    public void TheFaderIsOneAndNeverFades()
    {
        StageTransitions.Enabled = true;
        using var s = Show();
        var item = s.Vm.Sliders.Items[0];
        item.LiveLevel = 0.25;
        item.Wiring!.RawLevel = 0.75;
        Pump(s.Window, 50);

        foreach (var (page, mode, level) in new[] { ("controls", StageMode.Controls, 0.25), ("controller", StageMode.Hardware, 0.75),
                                                    ("controls", StageMode.Controls, 0.25) })
        {
            s.Window.ShowPage(page);
            Settle(s.Window);   // the crossfade has started
            Assert.Equal(mode, s.Stage.Mode);
            var tile = s.Stage.Tiles().OfType<SliderTile>().First();
            Assert.Same(item, tile.DataContext);
            var fader = In<Panel>(tile, "Fader");
            var knob = Assert.Single(fader.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("Knob"));
            Assert.Single(fader.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("LevelFill"));
            // Neither the knob nor anything between it and the tile is a crossfaded layer.
            for (Visual? v = knob; v is not null && !ReferenceEquals(v, tile); v = v.GetVisualParent())
            {
                var control = (Control)v;
                Assert.False(control.Classes.Contains(StageTransitions.ControlsLayer) || control.Classes.Contains(StageTransitions.WiringLayer),
                             $"{control.GetType().Name} {control.Name} is a layer");
                Assert.Equal(1.0, control.Opacity);
                Assert.False(control.Transitions?.OfType<Avalonia.Animation.TransitionBase>().Any(t => t.Property == Visual.OpacityProperty) == true,
                             $"{control.GetType().Name} {control.Name} fades");
            }
            Assert.Equal((fader.Bounds.Height - 15) * level, knob.Margin.Bottom, 1);   // its level, at once
            Pump(s.Window, 250);
            Assert.Equal(1.0, DrawnOpacity(knob), 3);
        }
    }

    // ── 4. The side panel after General (spec §3.3), with the crossfades on ─────────────────

    [AvaloniaFact]
    public void TheSidePanelShowsAfterGeneral()
    {
        StageTransitions.Enabled = true;
        using var s = Show();
        s.Vm.Controls.Select(s.Vm.Sliders.Items[2]);
        s.Window.ShowPage("controls");     // the side panel crossfades: Hardware's panel out, the inspector in
        Pump(s.Window);
        s.Window.ShowPage("controller");   // and back
        Pump(s.Window);

        s.Window.ShowPage("general");
        Pump(s.Window);
        s.Window.ShowPage("controls");
        Pump(s.Window);
        var inspector = s.Stage.GetVisualDescendants().OfType<SliderInspector>().Single();
        Assert.Same(s.Vm.Sliders.Items[2], inspector.DataContext);
        AssertDrawn(inspector);
        AssertDrawn(In<TextBlock>(s.Stage, "PageTitle"));
        Assert.Equal("Controls", In<TextBlock>(s.Stage, "PageTitle").Text);

        Click(s, s.Stage.Tiles().OfType<SliderTile>().ElementAt(3));   // a new selection shows too
        inspector = s.Stage.GetVisualDescendants().OfType<SliderInspector>().Single();
        Assert.Same(s.Vm.Sliders.Items[3], inspector.DataContext);
        AssertDrawn(inspector);

        s.Window.ShowPage("general");
        Pump(s.Window);
        s.Window.ShowPage("controller");
        Pump(s.Window);
        var host = (HardwareInspectorHost)s.Stage.HardwareSidePanel!;
        AssertDrawn(host);
        AssertDrawn(In<Volco.App.Views.Hardware.DeviceCard>(host, "Card"));
        Assert.Equal(3, Assert.IsType<SliderWiringViewModel>(s.Vm.Hardware.Inspected).Index);
        AssertDrawn(host.GetVisualDescendants().OfType<SliderWiringInspector>().Single());
        AssertDrawn(In<TextBlock>(s.Stage, "PageTitle"));
        Assert.Equal("Hardware", In<TextBlock>(s.Stage, "PageTitle").Text);
        AssertDrawn(In<Button>(s.Stage, "ControllerSettingsChip"));

        Click(s, s.Stage.Tiles().OfType<SliderTile>().ElementAt(1));
        Assert.Equal(1, Assert.IsType<SliderWiringViewModel>(s.Vm.Hardware.Inspected).Index);
        AssertDrawn(host.GetVisualDescendants().OfType<SliderWiringInspector>().Single());

        // Without animations (Windows animations off) it is the same.
        StageTransitions.Enabled = false;
        s.Window.ShowPage("general");
        Settle(s.Window);
        s.Window.ShowPage("controls");
        Settle(s.Window);
        AssertDrawn(s.Stage.GetVisualDescendants().OfType<SliderInspector>().Single());
    }
}
