using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.ViewModels;
using Volco.App.Views.Pages;
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.App.Tests.Pages;

public class SlidersPageTests
{
    private static FakeAudioEngine EngineWithAudio() => new()
    {
        Devices =
        {
            new AudioDevice("dev-1", "Speakers", AudioFlow.Render, true),
            new AudioDevice("dev-2", "Headphones", AudioFlow.Render, false),
        },
        Sessions =
        {
            new AudioSessionInfo("s1", 10, "spotify.exe", "dev-1", false),
            new AudioSessionInfo("s2", 11, "chrome.exe", "dev-1", false),
            new AudioSessionInfo("s3", 12, "discord.exe", "dev-2", false),
        },
    };

    private static (SlidersPage Page, Window Window, TestRig Rig, SettingsViewModel Vm) Show(Action<AppConfig>? configure = null)
    {
        var rig = new TestRig(EngineWithAudio());
        configure?.Invoke(rig.Config.Config);
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new SlidersPage(vm.Sliders);
        var window = new Window { Content = page, Width = 900, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (page, window, rig, vm);
    }

    private static List<T> All<T>(Control root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().Where(c => c.Name == name).ToList();

    private static Border Card(SlidersPage page, int index) => All<Border>(page, "Card")[index];

    private static T In<T>(SlidersPage page, int card, string name) where T : Control
        => Card(page, card).GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static void Expand(SettingsViewModel vm, int index)
    {
        vm.Sliders.Items[index].IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void SlidersPage_RendersOneCardPerSlider_NumberedByPosition()
    {
        var (page, _, rig, vm) = Show();
        using var _ = rig; using var __ = vm;

        Assert.Equal(rig.Config.Config.Sliders.Count, All<Border>(page, "Card").Count);
        Assert.Equal(new[] { "Slider 0", "Slider 1", "Slider 2", "Slider 3", "Slider 4" },
            All<TextBlock>(page, "TitleText").Select(t => t.Text).ToArray());
        // A new config is all "Not set up".
        Assert.All(All<TextBlock>(page, "SummaryText"), t => Assert.Equal("Not set up", t.Text));
        Assert.All(All<Border>(page, "Card"), c => Assert.Contains("unset", c.Classes));
    }

    [AvaloniaFact]
    public void SlidersPage_CardsStartClosed_AndTheHeaderOpensThem()
    {
        var (page, _, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        var body = In<Border>(page, 0, "Body");
        Assert.False(body.IsVisible);

        var header = In<Button>(page, 0, "ExpandButton");
        header.Command!.Execute(header.CommandParameter);

        Assert.True(body.IsVisible);
    }

    [AvaloniaFact]
    public void SlidersPage_Summary_DescribesWhatEachSliderControls()
    {
        var (page, _, rig, vm) = Show(c =>
        {
            c.Sliders[0].Type = SliderMappingType.Processes;
            c.Sliders[0].Processes = new List<string> { "spotify.exe", "chrome.exe" };
            c.Sliders[1].Type = SliderMappingType.Unmapped;
            c.Sliders[2].Type = SliderMappingType.Device;
            c.Sliders[3].Type = SliderMappingType.Device;
            c.Sliders[3].Device = new DeviceRefConfig { Id = "dev-2", FriendlyName = "Headphones" };
        });
        using var _ = rig; using var __ = vm;

        Assert.Equal(new[] { "spotify.exe, chrome.exe", "All other apps", "Current default output", "Headphones", "Not set up" },
            All<TextBlock>(page, "SummaryText").Select(t => t.Text).ToArray());
        Assert.DoesNotContain("unset", Card(page, 0).Classes);
    }

    // ── What it controls ──────────────────────────────────────────────────────

    [AvaloniaFact]
    public void SlidersPage_NotSetUp_ShowsNoChoiceAndAHint()
    {
        var (page, _, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        Expand(vm, 0);

        Assert.True(In<TextBlock>(page, 0, "NotSetUpHint").IsVisible);
        foreach (var option in new[] { "AppsOption", "OtherAppsOption", "DeviceOption" })
            Assert.NotEqual(true, In<RadioButton>(page, 0, option).IsChecked);
        Assert.False(In<Grid>(page, 0, "AppsSection").IsVisible);
        Assert.False(In<Grid>(page, 0, "DeviceSection").IsVisible);
    }

    [AvaloniaFact]
    public void SlidersPage_ChoosingWhatItControls_ShowsThatSection_AndSaves()
    {
        var (page, _, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        Expand(vm, 0);

        In<RadioButton>(page, 0, "AppsOption").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SliderMappingType.Processes, rig.Config.Config.Sliders[0].Type);
        Assert.True(In<Grid>(page, 0, "AppsSection").IsVisible);
        Assert.False(In<TextBlock>(page, 0, "NotSetUpHint").IsVisible);

        In<RadioButton>(page, 0, "DeviceOption").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SliderMappingType.Device, rig.Config.Config.Sliders[0].Type);
        Assert.False(In<Grid>(page, 0, "AppsSection").IsVisible);
        Assert.True(In<Grid>(page, 0, "DeviceSection").IsVisible);
        Assert.NotEqual(true, In<RadioButton>(page, 0, "AppsOption").IsChecked);

        In<RadioButton>(page, 0, "OtherAppsOption").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SliderMappingType.Unmapped, rig.Config.Config.Sliders[0].Type);
        Assert.True(In<TextBlock>(page, 0, "OtherAppsHint").IsVisible);
    }

    [AvaloniaFact]
    public void SlidersPage_AllOtherApps_HintNamesTheAppsOtherSlidersClaim()
    {
        var (page, _, rig, vm) = Show(c =>
        {
            c.Sliders[0].Type = SliderMappingType.Processes;
            c.Sliders[0].Processes = new List<string> { "spotify.exe" };
            c.Sliders[1].Type = SliderMappingType.Unmapped;
        });
        using var _ = rig; using var __ = vm;
        Expand(vm, 1);

        Assert.Contains("everything except spotify.exe", In<TextBlock>(page, 1, "OtherAppsHint").Text);

        // Adding an app elsewhere updates the hint.
        vm.Sliders.Items[0].AddApp("chrome.exe");
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("spotify.exe, chrome.exe", In<TextBlock>(page, 1, "OtherAppsHint").Text);
    }

    // ── Devices ───────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void SlidersPage_DevicePicker_OffersCurrentDefaultFirst_AndWritesThrough()
    {
        var (page, _, rig, vm) = Show(c => c.Sliders[0].Type = SliderMappingType.Device);
        using var _ = rig; using var __ = vm;
        Expand(vm, 0);
        var picker = In<ComboBox>(page, 0, "DeviceCombo");

        Assert.Equal(new[] { SliderItemViewModel.CurrentDefaultOutput, "Speakers", "Headphones" },
            Assert.IsAssignableFrom<IEnumerable<string>>(picker.ItemsSource));
        Assert.Equal(SliderItemViewModel.CurrentDefaultOutput, picker.SelectedItem);

        picker.SelectedItem = "Headphones";
        Assert.Equal("dev-2", rig.Config.Config.Sliders[0].Device!.Id);

        picker.SelectedItem = SliderItemViewModel.CurrentDefaultOutput;
        Assert.Null(rig.Config.Config.Sliders[0].Device);
    }

    [AvaloniaFact]
    public void SlidersPage_FriendlyNameOnlyDevice_IsSelected()
    {
        var (page, _, rig, vm) = Show(c =>
        {
            c.Sliders[0].Type = SliderMappingType.Device;
            c.Sliders[0].Device = new DeviceRefConfig { Id = "", FriendlyName = "Speakers" };
        });
        using var _ = rig; using var __ = vm;
        Expand(vm, 0);

        Assert.Equal("Speakers", In<ComboBox>(page, 0, "DeviceCombo").SelectedItem);
    }

    // ── App chips ─────────────────────────────────────────────────────────────

    private static (AutoCompleteBox Box, Window Window) TypeInProcessBox(SlidersPage page, string text)
    {
        var box = In<AutoCompleteBox>(page, 0, "ProcessBox");
        var window = (Window)TopLevel.GetTopLevel(page)!;
        box.Focus();
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
        return (box, window);
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void SlidersPage_PickingASuggestion_AddsAChipAndClearsTheBox()
    {
        var (page, _, rig, vm) = Show(c => c.Sliders[0].Type = SliderMappingType.Processes);
        using var _ = rig; using var __ = vm;
        Expand(vm, 0);

        var (box, window) = TypeInProcessBox(page, "chr");
        Assert.True(box.IsDropDownOpen);
        Press(window, Key.Down);
        Press(window, Key.Enter);

        Assert.Equal(new[] { "chrome.exe" }, rig.Config.Config.Sliders[0].Processes);
        Assert.Equal(new[] { "chrome.exe" }, vm.Sliders.Items[0].AppNames);
        Assert.True(string.IsNullOrEmpty(box.Text));
    }

    [AvaloniaFact]
    public void SlidersPage_EnterAddsWhatWasTyped_AndDuplicatesAreIgnored()
    {
        var (page, _, rig, vm) = Show(c =>
        {
            c.Sliders[0].Type = SliderMappingType.Processes;
            c.Sliders[0].Processes = new List<string> { "Game.exe" };
        });
        using var _ = rig; using var __ = vm;
        Expand(vm, 0);

        var (box, window) = TypeInProcessBox(page, "vlc.exe");
        box.IsDropDownOpen = false;
        Press(window, Key.Enter);
        var (_, window2) = TypeInProcessBox(page, "game.exe");
        box.IsDropDownOpen = false;
        Press(window2, Key.Enter);

        Assert.Equal(new[] { "Game.exe", "vlc.exe" }, rig.Config.Config.Sliders[0].Processes);
    }

    [AvaloniaFact]
    public void SlidersPage_ChipRemoveButton_RemovesThatApp()
    {
        var (page, _, rig, vm) = Show(c =>
        {
            c.Sliders[0].Type = SliderMappingType.Processes;
            c.Sliders[0].Processes = new List<string> { "spotify.exe", "chrome.exe" };
        });
        using var _ = rig; using var __ = vm;
        Expand(vm, 0);

        var remove = All<Button>(Card(page, 0), "RemoveChipButton")[0];
        remove.Command!.Execute(remove.CommandParameter);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "chrome.exe" }, rig.Config.Config.Sliders[0].Processes);
        Assert.Equal("chrome.exe", In<TextBlock>(page, 0, "SummaryText").Text);
    }

    [AvaloniaFact]
    public void SlidersPage_NewSessionAppearsWhenTypingAgain()
    {
        var (page, _, rig, vm) = Show(c => c.Sliders[0].Type = SliderMappingType.Processes);
        using var _ = rig; using var __ = vm;
        Expand(vm, 0);
        var (box, window) = TypeInProcessBox(page, "ch");

        rig.Engine.Sessions.Add(new AudioSessionInfo("s4", 13, "chromium.exe", "dev-1", false));
        window.KeyTextInput("r");
        Dispatcher.UIThread.RunJobs();

        var suggestions = box.GetVisualDescendants().OfType<Popup>().Single().Child!
            .GetVisualDescendants().OfType<ListBox>().Single().Items.OfType<string>().OrderBy(x => x);
        Assert.Equal(new[] { "chrome.exe", "chromium.exe" }, suggestions);
    }

    // ── Advanced ──────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void SlidersPage_Advanced_IsCollapsedUntilOpened_AndWritesThrough()
    {
        var (page, _, rig, vm) = Show(c => c.Sliders[0].Type = SliderMappingType.Device);
        using var _ = rig; using var __ = vm;
        Expand(vm, 0);
        var section = In<Border>(page, 0, "AdvancedSection");
        Assert.False(section.IsVisible);

        In<ToggleButton>(page, 0, "AdvancedToggle").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(section.IsVisible);

        In<ToggleSwitch>(page, 0, "InvertSwitch").IsChecked = true;
        In<ComboBox>(page, 0, "CurveCombo").SelectedIndex = 3;
        In<Slider>(page, 0, "DeadZoneSlider").Value = 0.1234;
        In<Slider>(page, 0, "SmoothingSlider").Value = 0.876;

        var cfg = rig.Config.Config.Sliders[0];
        Assert.True(cfg.Invert);
        Assert.Equal(ResponseCurve.SCurve, cfg.Curve);
        Assert.Equal(0.12, cfg.DeadZonePercent);   // two decimals: the slider is continuous
        Assert.Equal(0.88, cfg.SmoothingFactor);
    }

    // ── Add / reset / remove ──────────────────────────────────────────────────

    [AvaloniaFact]
    public void SlidersPage_Add_AppendsANotSetUpSliderForTheNextInput_Opened()
    {
        var (page, _, rig, vm) = Show();
        using var _ = rig; using var __ = vm;

        var add = page.FindControl<Button>("AddButton")!;
        add.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(6, rig.Config.Config.Sliders.Count);
        Assert.Equal(SliderMappingType.None, rig.Config.Config.Sliders[5].Type);
        Assert.Equal("Slider 5", All<TextBlock>(page, "TitleText")[5].Text);
        Assert.True(In<Border>(page, 5, "Body").IsVisible);
        Assert.True(vm.Sliders.Items[5].IsLast);
        Assert.False(vm.Sliders.Items[4].IsLast);
    }

    [AvaloniaFact]
    public void SlidersPage_Reset_ClearsEverything_AndUndoRestoresIt()
    {
        var (page, _, rig, vm) = Show(c =>
        {
            c.Sliders[2].Type = SliderMappingType.Processes;
            c.Sliders[2].Processes = new List<string> { "spotify.exe" };
            c.Sliders[2].Invert = true;
            c.Sliders[2].Curve = ResponseCurve.Logarithmic;
        });
        using var _ = rig; using var __ = vm;
        var cfg = rig.Config.Config.Sliders[2];

        vm.Sliders.Items[2].ResetCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(SliderMappingType.None, cfg.Type);
        Assert.Empty(cfg.Processes);
        Assert.False(cfg.Invert);
        Assert.Equal(ResponseCurve.Linear, cfg.Curve);
        Assert.Same(cfg, rig.Config.Config.Sliders[2]);       // nothing moved
        Assert.Equal(5, rig.Config.Config.Sliders.Count);
        Assert.Equal("Not set up", In<TextBlock>(page, 2, "SummaryText").Text);
        Assert.True(vm.Toast.IsOpen);
        Assert.Equal("Slider 2 reset", vm.Toast.Message);

        vm.Toast.UndoCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(SliderMappingType.Processes, cfg.Type);
        Assert.Equal(new[] { "spotify.exe" }, cfg.Processes);
        Assert.True(cfg.Invert);
        Assert.Equal(ResponseCurve.Logarithmic, cfg.Curve);
        Assert.Equal("spotify.exe", In<TextBlock>(page, 2, "SummaryText").Text);
        Assert.False(vm.Toast.IsOpen);
    }

    [AvaloniaFact]
    public void SlidersPage_RemoveLast_IsOfferedOnlyOnTheLastCard_AndCanBeUndone()
    {
        var (page, _, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        Assert.Equal(new[] { false, false, false, false, true }, vm.Sliders.Items.Select(i => i.IsLast).ToArray());

        vm.Sliders.Items[4].RemoveLastCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(4, rig.Config.Config.Sliders.Count);
        Assert.Equal(4, All<Border>(page, "Card").Count);
        Assert.True(vm.Sliders.Items[3].IsLast);
        Assert.Equal("Slider 4 removed", vm.Toast.Message);

        vm.Toast.UndoCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(5, rig.Config.Config.Sliders.Count);
        Assert.True(vm.Sliders.Items[4].IsLast);
    }

    // ── Reordering: position is the hardware input ────────────────────────────

    [AvaloniaFact]
    public void SlidersPage_MoveDown_MovesTheConfigToTheNextInput()
    {
        var (page, _, rig, vm) = Show(c => c.Sliders[0].Type = SliderMappingType.Unmapped);
        using var _ = rig; using var __ = vm;
        var moved = rig.Config.Config.Sliders[0];

        vm.Sliders.Items[0].MoveDownCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(moved, rig.Config.Config.Sliders[1]);
        Assert.Equal("All other apps", All<TextBlock>(page, "SummaryText")[1].Text);
        Assert.Equal(new[] { "Slider 0", "Slider 1", "Slider 2", "Slider 3", "Slider 4" },
            All<TextBlock>(page, "TitleText").Select(t => t.Text).ToArray());
    }

    [AvaloniaFact]
    public void SlidersPage_AltDownOnAGrip_MovesThatCard_AndKeepsFocusOnIt()
    {
        var (page, window, rig, vm) = Show(c => c.Sliders[1].Type = SliderMappingType.Unmapped);
        using var _ = rig; using var __ = vm;
        var moved = rig.Config.Config.Sliders[1];
        var grip = In<Border>(page, 1, "Grip");
        Assert.True(grip.Focusable);
        grip.Focus();
        Dispatcher.UIThread.RunJobs();

        Press(window, Key.Down, RawInputModifiers.Alt);

        Assert.Same(moved, rig.Config.Config.Sliders[2]);
        Assert.Equal("All other apps", vm.Sliders.Items[2].Summary);
        Assert.Same(In<Border>(page, 2, "Grip"), window.FocusManager?.GetFocusedElement());
    }

    [AvaloniaFact]
    public void SlidersPage_DraggingAGrip_RelabelsLive_AndMovesOnDrop()
    {
        var (page, window, rig, vm) = Show(c => c.Sliders[0].Type = SliderMappingType.Unmapped);
        using var _ = rig; using var __ = vm;
        var moved = rig.Config.Config.Sliders[0];
        var grip = In<Border>(page, 0, "Grip");
        var start = grip.TranslatePoint(new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), window)!.Value;
        var step = Card(page, 1).Bounds.Height + 8;

        window.MouseMove(start);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Point(0, step * 2.6));
        Dispatcher.UIThread.RunJobs();

        // Mid-drag: the dragged card already reads as input 2; nothing moved yet.
        Assert.True(page.Reorder.IsDragging);
        Assert.Equal(2, vm.Sliders.Items[0].Index);
        Assert.Equal(0, vm.Sliders.Items[1].Index);
        Assert.Same(moved, rig.Config.Config.Sliders[0]);

        window.MouseUp(start + new Point(0, step * 2.6), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.False(page.Reorder.IsDragging);
        Assert.Same(moved, rig.Config.Config.Sliders[2]);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, vm.Sliders.Items.Select(i => i.Index).ToArray());
        Assert.Equal("All other apps", All<TextBlock>(page, "SummaryText")[2].Text);
    }

    [AvaloniaFact]
    public void SlidersPage_EscapeDuringDrag_PutsEverythingBack()
    {
        var (page, window, rig, vm) = Show(c => c.Sliders[0].Type = SliderMappingType.Unmapped);
        using var _ = rig; using var __ = vm;
        var moved = rig.Config.Config.Sliders[0];
        var grip = In<Border>(page, 0, "Grip");
        var start = grip.TranslatePoint(new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), window)!.Value;

        window.MouseMove(start);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Point(0, Card(page, 1).Bounds.Height * 2));
        Press(window, Key.Escape);
        window.MouseUp(start, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(moved, rig.Config.Config.Sliders[0]);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, vm.Sliders.Items.Select(i => i.Index).ToArray());
    }

    [Fact]
    public void PreviewIndex_ShiftsTheCardsBetweenFromAndTo()
    {
        // Dragging card 1 to position 3: 2 and 3 shift up, 1 lands at 3.
        Assert.Equal(new[] { 0, 3, 1, 2, 4 }, Enumerable.Range(0, 5).Select(i => SlidersViewModel.PreviewIndex(i, 1, 3)));
        // Dragging card 3 to position 1: 1 and 2 shift down.
        Assert.Equal(new[] { 0, 2, 3, 1, 4 }, Enumerable.Range(0, 5).Select(i => SlidersViewModel.PreviewIndex(i, 3, 1)));
    }

    // ── Live level ────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void SlidersPage_LiveLevel_UpdatesThatCardsBarAndFlashesIt()
    {
        var (page, _, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        var bar = In<ProgressBar>(page, 2, "LiveLevelBar");
        Assert.Equal(0, bar.Value);

        vm.Sliders.UpdateLiveLevel(2, 0.5f);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0.5, bar.Value, 3);
        Assert.Equal(1, bar.Maximum);
        Assert.Contains("active", Card(page, 2).Classes);
        Assert.DoesNotContain("active", Card(page, 1).Classes);
    }
}
