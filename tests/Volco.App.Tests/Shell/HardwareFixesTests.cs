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
    private static void Pump(Window window, int ms = 600)
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

    /// <summary>The opacity <paramref name="visual"/> is drawn with inside the stage: its own times
    /// every ancestor's up to the stage. (The window's own crossfade between General and the stage
    /// is above it.)</summary>
    private static double DrawnOpacity(Visual visual)
    {
        var opacity = 1.0;
        for (Visual? v = visual; v is not null; v = v.GetVisualParent())
        {
            opacity *= v.Opacity;
            if (v is StagePage) break;
        }
        return opacity;
    }

    /// <summary>The ancestors of <paramref name="visual"/> (up to the window) drawn faded, for a failure's message.</summary>
    private static string Faded(Visual visual)
        => string.Join(" < ", visual.GetSelfAndVisualAncestors().Where(v => v.Opacity < 1)
                                    .Select(v => $"{v.GetType().Name}{((v as Control)?.Name is { } n ? " " + n : "")} {v.Opacity:0.000}"));

    /// <summary><paramref name="control"/> is on screen: visible and fully opaque.</summary>
    private static void AssertDrawn(Control control)
    {
        Assert.True(control.IsEffectivelyVisible, $"{control.GetType().Name} is visible");
        Assert.True(Math.Abs(DrawnOpacity(control) - 1) < 0.001, $"{control.GetType().Name} is drawn faded: {Faded(control)}");
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

    /// <summary>Saves the draft with the save bar and answers as <paramref name="reply"/> says.</summary>
    private static void SaveAndAnswer(Shown s, Reply reply)
    {
        Press(In<Button>(In<SaveBar>(s.Stage, "SaveBarHost"), "SaveButton"));
        Answer(s, reply);
    }

    /// <summary>Answers the save that runs as <paramref name="reply"/> says, with a Hello of the
    /// saved setup and its counts.</summary>
    private static void Answer(Shown s, Reply reply)
    {
        var draft = s.Vm.Controller.Draft;
        var blob = ControllerConfigCodec.Encode(draft);
        var kinds = KindsOf(draft);
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
        Assert.StartsWith("Saved.", s.Vm.Controller.ProgressText);
    }

    /// <summary>The indicator kinds a controller holding <paramref name="config"/> reports in its Hello.</summary>
    private static IndicatorKind[] KindsOf(ControllerConfig config) => config.Buttons.Select(b => b.IndicatorCase switch
    {
        Volco.Core.Controller.Proto.Button.IndicatorOneofCase.Led => IndicatorKind.Led,
        Volco.Core.Controller.Proto.Button.IndicatorOneofCase.LedPair => IndicatorKind.LedPair,
        Volco.Core.Controller.Proto.Button.IndicatorOneofCase.Screen => IndicatorKind.Screen,
        _ => IndicatorKind.None,
    }).ToArray();

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
        s.Vm.Hardware.Sections.SliderHardwareOpen = true;
        s.Vm.Hardware.Sections.ButtonHardwareOpen = true;
        Click(s, s.Stage.Tiles().OfType<TTile>().ElementAt(index));
        var host = (HardwareInspectorHost)s.Stage.HardwareSidePanel!;
        Press(In<Button>(s.Stage, "HardwareRemoveButton"));   // in the inspector's Hardware section
        Settle(s.Window);
        Press(In<Button>(host, "ConfirmButton"));
        Settle(s.Window);
    }

    /// <summary>To General and back: nothing unsaved, so no prompt asks, and the drawing is Volco's
    /// settings again.</summary>
    private static void ShowControls(Shown s)
    {
        s.Window.ShowPage("general");
        Pump(s.Window, 50);
        Assert.Null(s.Vm.Hardware.LeavePrompt);   // nothing unsaved
        Assert.IsType<GeneralPage>(s.Window.CurrentPage);
        s.Window.ShowPage("controller");
        Pump(s.Window, 50);
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

    [AvaloniaFact]
    public void RemovingThenSaveAndContinueShowsTheShiftedControls()
    {
        using var s = Show();
        RemoveWithTheInspector<SliderTile>(s, 2);
        var stage = s.Stage;
        s.Window.ShowPage("general");   // unsaved: the leave prompt asks
        Settle(s.Window);
        Press(In<Button>(stage, "SaveAndContinueButton"));
        Answer(s, Reply.OkThenHello);
        Until(s.Window, () => s.Window.CurrentPage is GeneralPage);
        s.Window.ShowPage("controller");
        Pump(s.Window, 50);

        Assert.Equal(new[] { "a", "b", "d", "e" }, s.Config.Sliders.Select(x => x.Name));
        Assert.Equal(new[] { "a", "b", "d", "e" }, Written(s).Sliders.Select(x => x.Name));
        Assert.Equal(new[] { "a", "b", "d", "e" }, TileNames(s));
        Assert.DoesNotContain(s.Vm.Sliders.Items, x => x.IsMissing);
    }

    // ── 2b. Removing after the setup came from the controller (follow-up) ───────────────────

    private static ControllerConfig DiyConfig() => ControllerPresets.ApplyTo(new ControllerConfig(), Diy);

    /// <summary>The DIY setup with Slider 2 on another pin: a different setup with the same counts.</summary>
    private static ControllerConfig DiyMoved()
    {
        var c = DiyConfig();
        c.Sliders[1].Pin = c.Sliders[1].Pin == 35 ? 39u : 35u;
        return c;
    }

    /// <summary>The DIY setup with only its first three sliders.</summary>
    private static ControllerConfig DiyThree()
    {
        var c = DiyConfig();
        while (c.Sliders.Count > 3) c.Sliders.RemoveAt(c.Sliders.Count - 1);
        return c;
    }

    private static List<SliderConfig> Named(string names)
        => names.Select((n, i) => new SliderConfig { Index = i, Name = n.ToString(), Type = SliderMappingType.None }).ToList();

    private static int Reads(Shown s) => s.Conn.SentFrames.Count(f => f.Length > 3 && f[2] == 0x87);

    /// <summary>Waits for read number <paramref name="nth"/> and answers it with <paramref name="blob"/>.</summary>
    private static void AnswerRead(Shown s, int nth, byte[] blob)
    {
        Until(s.Window, () => Reads(s) >= nth);
        var seq = s.Conn.SentFrames.Last(f => f.Length > 3 && f[2] == 0x87)[3];
        s.Conn.RaiseMessage(new ConfigDataMessage(seq, (ushort)blob.Length, 0, ControllerConfigCodec.Crc32(blob), blob));
        Settle(s.Window);
    }

    /// <summary>The settings window over Volco's settings as <paramref name="setUp"/> leaves them
    /// (or as <paramref name="configJson"/> has them on disk), Volco's copy <paramref name="mirror"/>,
    /// and a controller holding <paramref name="controller"/> (null: not set up). With
    /// <paramref name="pagesFirst"/> the Hardware page is open before the controller says Hello. A
    /// controller with a setup and no copy in Volco gets the automatic read, answered here.</summary>
    private static Shown Open(Action<AppConfig>? setUp, ControllerConfig? mirror, ControllerConfig? controller,
                              bool pagesFirst = false, string? configJson = null)
    {
        var rig = new TestRig(configJson: configJson);
        setUp?.Invoke(rig.Config.Config);
        if (mirror is not null) rig.Config.Config.ControllerConfigBlob = Convert.ToBase64String(ControllerConfigCodec.Encode(mirror));
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var window = new SettingsWindow(vm, rig.Runtime) { Width = 1100, Height = 720 };
        window.Show();
        var s = new Shown(window, rig, vm);
        if (pagesFirst)
        {
            window.ShowPage("controller");
            Pump(window, 50);
        }
        rig.Connection!.SetStatus(TransportStatus.Open);
        var blob = controller is null ? null : ControllerConfigCodec.Encode(controller);
        rig.Connection.RaiseMessage(blob is null
            ? new HelloMessage(ProtocolVersion.Current, ChipFamily.Esp32, "dev", ControllerConfigState.None, 0, 0, Array.Empty<IndicatorKind>())
            : Hello(ControllerConfigCodec.Crc32(blob), controller!.Sliders.Count, KindsOf(controller)));
        Pump(window, 50);
        if (blob is not null && mirror is null) AnswerRead(s, 1, blob);   // the automatic read adopts it
        window.ShowPage("controller");
        Pump(window, 100);
        return s;
    }

    /// <summary>Waits until the draft can be edited; the message says where it stopped.</summary>
    private static void UntilEditable(Shown s)
    {
        try { Until(s.Window, () => s.Vm.Controller.CanEdit, 3000); }
        catch (TimeoutException)
        {
            var c = s.Vm.Controller;
            Assert.Fail($"not editable: banner {c.Banner.Kind}, in sync {c.InSync}, setting up {c.IsSettingUp}, " +
                        $"draft {c.Draft.Sliders.Count} sliders, chooser {s.Vm.Hardware.ShowChooser}");
        }
    }

    /// <summary>Removes slider <paramref name="index"/> with the inspector, saves, confirms, then
    /// shows Controls and checks config.json and the tiles hold <paramref name="expected"/> in order
    /// ("" is a slider with no name), none drawn missing.</summary>
    private static void RemoveSaveAndCheck(Shown s, int index, string[] expected)
    {
        UntilEditable(s);
        var before = s.Vm.Controller.Draft.Sliders.Count;
        RemoveWithTheInspector<SliderTile>(s, index);
        Assert.Equal(before - 1, s.Vm.Controller.Draft.Sliders.Count);
        SaveAndAnswer(s, Reply.OkThenHello);
        ShowControls(s);

        Assert.Equal(expected, s.Config.Sliders.Select(x => x.Name));
        Assert.Equal(Enumerable.Range(0, expected.Length), s.Config.Sliders.Select(x => x.Index));
        Assert.Equal(expected, Written(s).Sliders.Select(x => x.Name));
        Assert.Equal(expected, TileNames(s));
        Assert.DoesNotContain(s.Vm.Sliders.Items, x => x.IsMissing);
        Assert.Equal(expected.Length, s.Rig.Config.Config.Controller!.Sliders);
    }

    [AvaloniaFact]
    public void A_MoreSlidersInVolcoThanOnTheController()
    {
        using var s = Open(c => c.Sliders = Named("abcdefg"), DiyConfig(), DiyConfig());
        Assert.Equal(2, s.Vm.Sliders.Items.Count(x => x.IsMissing));   // f and g: not on this controller
        RemoveSaveAndCheck(s, 2, new[] { "a", "b", "d", "e" });
    }

    [AvaloniaFact]
    public void B_FewerSlidersInVolcoThanOnTheController()
    {
        using var s = Open(c => c.Sliders = Named("abc"), DiyConfig(), DiyConfig());
        Assert.Equal(5, s.Config.Sliders.Count);   // padded
        RemoveSaveAndCheck(s, 1, new[] { "a", "c", "", "" });
    }

    /// <summary>Compare › Keep the controller's setup: two reads, both answered with <paramref name="controller"/>.</summary>
    private static void KeepTheControllers(Shown s, ControllerConfig controller)
    {
        var blob = ControllerConfigCodec.Encode(controller);
        var before = Reads(s);
        s.Vm.Hardware.OpenCompare();
        var compare = s.Vm.Hardware.Compare!;
        AnswerRead(s, before + 1, blob);
        Until(s.Window, () => compare.HasComparison);
        compare.KeepCommand.Execute(null);
        AnswerRead(s, before + 2, blob);
        Until(s.Window, () => !s.Vm.Hardware.IsCompareOpen && s.Vm.Controller.InSync);
        Pump(s.Window, 50);
    }

    /// <summary>Picks the setup chooser's card titled <paramref name="title"/>, as a click on it does.</summary>
    private static void Pick(Shown s, string title)
    {
        Assert.True(s.Vm.Hardware.ShowChooser);
        s.Vm.Hardware.Chooser.Cards.Single(c => c.Title == title).PickCommand.Execute(null);
        Pump(s.Window, 50);
        Assert.False(s.Vm.Hardware.ShowChooser);
    }

    [AvaloniaFact]
    public void C_KeepTheControllersSetup_WithACleanDraft()
    {
        using var s = Open(c => c.Sliders = Named("abcde"), DiyConfig(), DiyThree());
        Assert.Equal(BannerKind.DifferentSetup, s.Vm.Controller.Banner.Kind);
        KeepTheControllers(s, DiyThree());
        Assert.Equal(3, s.Vm.Controller.Draft.Sliders.Count);   // the draft follows the adopted setup
        RemoveSaveAndCheck(s, 1, new[] { "a", "c" });
    }

    [AvaloniaFact]
    public void C_KeepTheControllersSetup_WithADirtyDraft()
    {
        using var s = Open(c => c.Sliders = Named("abcde"), DiyConfig(), DiyThree());
        s.Vm.Hardware.StartOver();
        Settle(s.Window);
        Pick(s, "Volco's saved copy");   // Volco's copy, as a new draft
        UntilEditable(s);
        s.Vm.Controller.SetSliderPin(1, 39);
        Assert.True(s.Vm.Controller.IsDirty);
        KeepTheControllers(s, DiyThree());
        Assert.Equal(5, s.Vm.Controller.Draft.Sliders.Count);   // a dirty draft stays
        RemoveSaveAndCheck(s, 2, new[] { "a", "b", "d", "e" });
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void D_NoCopyInVolco_TheAutomaticReadAdoptsTheControllers(bool pagesFirst)
    {
        using var s = Open(c => c.Sliders = Named("abcde"), mirror: null, DiyConfig(), pagesFirst);
        Until(s.Window, () => s.Vm.Controller.InSync);
        Assert.Equal(5, s.Vm.Controller.Draft.Sliders.Count);   // the draft follows the adopted setup
        RemoveSaveAndCheck(s, 2, new[] { "a", "b", "d", "e" });
    }

    [AvaloniaFact]
    public void E_ChooserSavedCopy_WithFewerSlidersInVolco()
    {
        using var s = Open(c => c.Sliders = Named("abc"), DiyConfig(), controller: null);
        Pick(s, "Volco's saved copy");
        RemoveSaveAndCheck(s, 1, new[] { "a", "c", "", "" });
    }

    [AvaloniaFact]
    public void E_ChooserPreset_WithMoreSlidersInVolco()
    {
        using var s = Open(c => c.Sliders = Named("abcdefg"), mirror: null, controller: null);
        Pick(s, Diy.DisplayName);
        RemoveSaveAndCheck(s, 2, new[] { "a", "b", "d", "e" });
    }

    [AvaloniaFact]
    public void E_ChooserStartEmpty_ThenAdded()
    {
        using var s = Open(c => c.Sliders = Named("abc"), mirror: null, controller: null);
        Pick(s, "Start empty");
        UntilEditable(s);
        uint[] pins = { 32, 33, 34 };
        foreach (var pin in pins)
        {
            s.Vm.Controller.AddSliderCommand.Execute(null);
            s.Vm.Controller.SetSliderPin(s.Vm.Controller.Draft.Sliders.Count - 1, pin);
        }
        Settle(s.Window);
        RemoveSaveAndCheck(s, 1, new[] { "", "" });   // added controls are new
    }

    [AvaloniaFact]
    public void F_IndexesThatDontMatchTheirPositions()
    {
        var old = AppConfig.CreateDefault();
        old.Sliders = Named("abcde");
        for (int i = 0; i < old.Sliders.Count; i++) old.Sliders[i].Index = 4 - i;
        var json = System.Text.Json.JsonSerializer.Serialize(old, ConfigJsonContext.Default.AppConfig);
        Assert.Contains("\"index\": 4", json);
        using var s = Open(null, DiyConfig(), DiyConfig(), configJson: json);
        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, TileNames(s));
        RemoveSaveAndCheck(s, 2, new[] { "a", "b", "d", "e" });
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void F_ALastHelloThatDoesntMatchVolcosCopy(bool pagesFirst)
    {
        using var s = Open(c =>
        {
            c.Sliders = Named("abcde");
            c.Controller = new ControllerInfoConfig
            {
                ProtocolVersion = ProtocolVersion.Current, Chip = ChipFamily.Esp32, FirmwareVersion = "old", Sliders = 8,
                Buttons = Enumerable.Repeat(IndicatorKind.Led, 6).ToList(),
            };
        }, DiyConfig(), DiyConfig(), pagesFirst);
        RemoveSaveAndCheck(s, 2, new[] { "a", "b", "d", "e" });
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

        // The live level, then during a hardware test the raw one, then the live one again.
        foreach (var (testing, level) in new[] { (false, 0.25), (true, 0.75), (false, 0.25) })
        {
            if (testing && !s.Vm.Hardware.IsTesting) s.Vm.Hardware.StartTestCommand.Execute(null);
            else if (!testing && s.Vm.Hardware.IsTesting) s.Vm.Hardware.StopTestCommand.Execute(null);
            Until(s.Window, () => s.Vm.Hardware.IsTesting == testing);
            item.Wiring!.RawLevel = 0.75;
            Settle(s.Window);   // the layers' crossfade has started
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

    // ── 4. The side panel after General (hardware-page spec §3.3), with the crossfades on ───

    /// <summary>To General and back to <paramref name="tag"/>. The hop to General swaps at once, so
    /// the stage is surely out of the window, as it is once the window's crossfade has ended (the
    /// headless platform doesn't always end that one the way Windows does); the way back fades.</summary>
    private static void ViaGeneral(Shown s, string tag)
    {
        var stage = s.Stage;
        StageTransitions.Enabled = false;
        s.Window.ShowPage("general");
        Settle(s.Window);
        Assert.Null(TopLevel.GetTopLevel(stage));   // the stage left the window
        StageTransitions.Enabled = true;
        s.Window.ShowPage(tag);
        Pump(s.Window);
        Assert.Same(stage, s.Stage);
    }

    [AvaloniaFact]
    public void TheSidePanelShowsAfterGeneral()
    {
        StageTransitions.Enabled = true;
        using var s = Show();
        s.Vm.Hardware.Sections.SliderHardwareOpen = true;   // the wiring shows in the tile's inspector
        s.Vm.Controls.Select(s.Vm.Sliders.Items[2]);
        Pump(s.Window);

        ViaGeneral(s, "controller");
        var inspector = s.Stage.GetVisualDescendants().OfType<SliderInspector>().Single();
        Assert.Same(s.Vm.Sliders.Items[2], inspector.DataContext);
        AssertDrawn(inspector);
        AssertDrawn(In<Volco.App.Views.Hardware.DeviceCard>(s.Window, "Card"));
        Assert.Equal(2, Assert.IsType<SliderWiringViewModel>(s.Vm.Hardware.Inspected).Index);
        AssertDrawn(s.Stage.GetVisualDescendants().OfType<SliderWiringInspector>().Single());
        Assert.Equal("Controller", In<TextBlock>(s.Stage, "PageTitle").Text);
        AssertDrawn(In<TextBlock>(s.Stage, "PageTitle"));
        AssertDrawn(In<Button>(s.Stage, "ControllerSettingsChip"));

        Click(s, s.Stage.Tiles().OfType<SliderTile>().ElementAt(3));   // a new selection shows too
        inspector = s.Stage.GetVisualDescendants().OfType<SliderInspector>().Single();
        Assert.Same(s.Vm.Sliders.Items[3], inspector.DataContext);
        AssertDrawn(inspector);

        ViaGeneral(s, "controller");   // again
        AssertDrawn(s.Stage.GetVisualDescendants().OfType<SliderInspector>().Single());
        AssertDrawn(s.Stage.GetVisualDescendants().OfType<SliderWiringInspector>().Single());
        AssertDrawn(In<Button>(s.Stage, "ControllerSettingsChip"));

        // Controller settings fills the side panel under the card.
        Click(s, In<Button>(s.Stage, "ControllerSettingsChip"));
        AssertDrawn(host.GetVisualDescendants().OfType<ControllerSettingsInspector>().Single());

        // With the crossfades left on for the hop to General too, the tile's inspector shows.
        s.Window.ShowPage("general");
        Pump(s.Window);
        s.Window.ShowPage("controller");
        Pump(s.Window);
        AssertDrawn(s.Stage.GetVisualDescendants().OfType<SliderInspector>().Single());
    }
}
