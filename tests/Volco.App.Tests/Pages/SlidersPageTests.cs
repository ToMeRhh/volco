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

    private static (SlidersPage Page, TestRig Rig, SettingsViewModel Vm) Show(Action<AppConfig>? configure = null)
    {
        var rig = new TestRig(EngineWithAudio());
        configure?.Invoke(rig.Config.Config);
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new SlidersPage(vm.Sliders);
        new Window { Content = page }.Show();
        Dispatcher.UIThread.RunJobs();
        return (page, rig, vm);
    }

    private static List<T> All<T>(SlidersPage page, string name) where T : Control
        => page.GetVisualDescendants().OfType<T>().Where(c => c.Name == name).ToList();

    [AvaloniaFact]
    public void SlidersPage_RendersOneCardPerConfiguredSlider()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;

        Assert.Equal(rig.Config.Config.Sliders.Count, All<ProgressBar>(page, "LiveLevelBar").Count);
        Assert.Equal(rig.Config.Config.Sliders.Count, vm.Sliders.Items.Count);
    }

    [AvaloniaFact]
    public void SlidersPage_MappingTypeDevice_ShowsDevicePickerOnly()
    {
        var (page, rig, vm) = Show(c => c.Sliders[0].Type = SliderMappingType.Device);
        using var _ = rig; using var __ = vm;

        Assert.True(All<StackPanel>(page, "DeviceSection")[0].IsVisible);
        Assert.False(All<StackPanel>(page, "ProcessesSection")[0].IsVisible);
    }

    [AvaloniaFact]
    public void SlidersPage_MappingTypeProcesses_ShowsProcessListOnly()
    {
        var (page, rig, vm) = Show(c => c.Sliders[0].Type = SliderMappingType.Processes);
        using var _ = rig; using var __ = vm;

        Assert.True(All<StackPanel>(page, "ProcessesSection")[0].IsVisible);
        Assert.False(All<StackPanel>(page, "DeviceSection")[0].IsVisible);

        // Switching the combo flips which section shows.
        All<ComboBox>(page, "MappingCombo")[0].SelectedIndex = 1;
        Assert.False(All<StackPanel>(page, "ProcessesSection")[0].IsVisible);
        Assert.True(All<StackPanel>(page, "DeviceSection")[0].IsVisible);
    }

    private static (AutoCompleteBox Box, Window Window) TypeInProcessBox(SlidersPage page, string text)
    {
        var box = All<AutoCompleteBox>(page, "ProcessBox")[0];
        var window = (Window)TopLevel.GetTopLevel(page)!;
        box.Focus();
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
        return (box, window);
    }

    /// <summary>The dropdown's list, which lives in the popup rather than under the box.</summary>
    private static ListBox DropDownList(AutoCompleteBox box)
        => box.GetVisualDescendants().OfType<Popup>().Single().Child!
            .GetVisualDescendants().OfType<ListBox>().Single();

    private static string[] Suggestions(AutoCompleteBox box)
        => DropDownList(box).Items.OfType<string>().OrderBy(x => x).ToArray();

    private static void AssertCommitted(AutoCompleteBox box, TestRig rig, SettingsViewModel vm)
    {
        Assert.Equal("discord.exe, chrome.exe", box.Text);
        Assert.Equal("discord.exe, chrome.exe", vm.Sliders.Items[0].ProcessesText);
        Assert.Equal(new[] { "discord.exe", "chrome.exe" }, rig.Config.Config.Sliders[0].Processes);
    }

    [AvaloniaFact]
    public void SlidersPage_ProcessAutoComplete_CommittingSuggestionKeepsEarlierTokens()
    {
        var (page, rig, vm) = Show(c => c.Sliders[0].Type = SliderMappingType.Processes);
        using var _ = rig; using var __ = vm;

        // Real commit path: type, let the dropdown populate, arrow to the suggestion, press Enter.
        var (box, window) = TypeInProcessBox(page, "discord.exe, ch");
        Assert.True(box.IsDropDownOpen);
        // Filtered on the last token only.
        Assert.Equal(new[] { "chrome.exe" }, Suggestions(box));

        Press(window, Key.Down);
        Dispatcher.UIThread.RunJobs();
        Press(window, Key.Enter);
        Dispatcher.UIThread.RunJobs();

        AssertCommitted(box, rig, vm);
    }

    [AvaloniaFact]
    public void SlidersPage_ProcessAutoComplete_NewSessionDuringPick_DoesNotRevertCommit()
    {
        var (page, rig, vm) = Show(c => c.Sliders[0].Type = SliderMappingType.Processes);
        using var _ = rig; using var __ = vm;

        var (box, window) = TypeInProcessBox(page, "discord.exe, ch");
        // A new app starts audio between populating the dropdown and committing the pick.
        rig.Engine.Sessions.Add(new AudioSessionInfo("s4", 13, "chromium.exe", "dev-1", false));

        Press(window, Key.Down);
        Dispatcher.UIThread.RunJobs();
        Press(window, Key.Enter);
        Dispatcher.UIThread.RunJobs();

        AssertCommitted(box, rig, vm);
    }

    [AvaloniaFact]
    public void SlidersPage_ProcessAutoComplete_PointerClickOnSuggestionKeepsEarlierTokens()
    {
        var (page, rig, vm) = Show(c => c.Sliders[0].Type = SliderMappingType.Processes);
        using var _ = rig; using var __ = vm;

        var (box, window) = TypeInProcessBox(page, "discord.exe, ch");
        Assert.True(box.IsDropDownOpen);
        var item = DropDownList(box).GetVisualDescendants().OfType<ListBoxItem>().Single();
        var point = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;

        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        AssertCommitted(box, rig, vm);
    }

    [AvaloniaFact]
    public void SlidersPage_ProcessAutoComplete_NewSessionAppearsWhenTypingAgain()
    {
        var (page, rig, vm) = Show(c => c.Sliders[0].Type = SliderMappingType.Processes);
        using var _ = rig; using var __ = vm;
        var (box, window) = TypeInProcessBox(page, "ch");
        Assert.Equal(new[] { "chrome.exe" }, Suggestions(box));

        rig.Engine.Sessions.Add(new AudioSessionInfo("s4", 13, "chromium.exe", "dev-1", false));
        window.KeyTextInput("r");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "chrome.exe", "chromium.exe" }, Suggestions(box));
    }

    private static void Press(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
    }

    [AvaloniaFact]
    public void SlidersPage_LiveLevel_UpdatesProgressBar()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        var bar = All<ProgressBar>(page, "LiveLevelBar")[2];
        Assert.Equal(0, bar.Value);

        vm.Sliders.UpdateLiveLevel(2, 0.5f);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0.5, bar.Value, 3);
        Assert.Equal(1, bar.Maximum);
    }

    [AvaloniaFact]
    public void SlidersPage_EditPersistsToConfig()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        Assert.False(rig.Config.Config.Sliders[0].Invert);

        All<CheckBox>(page, "InvertCheck")[0].IsChecked = true;

        Assert.True(rig.Config.Config.Sliders[0].Invert);
    }

    [AvaloniaFact]
    public void SlidersPage_ExpandButton_CollapsesBody()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        Assert.True(All<StackPanel>(page, "Body")[0].IsVisible);

        var button = All<Button>(page, "ExpandButton")[0];
        button.Command!.Execute(button.CommandParameter);

        Assert.False(All<StackPanel>(page, "Body")[0].IsVisible);
    }

    [AvaloniaFact]
    public void SlidersPage_DeadZoneSlider_QuantisesToHundredthsInConfig()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        var slider = All<Slider>(page, "DeadZoneSlider")[0];

        slider.Value = 0.1234;

        Assert.Equal(0.12, rig.Config.Config.Sliders[0].DeadZonePercent);
        Assert.Equal(0.12, slider.Value, 6);
    }

    [AvaloniaFact]
    public void SlidersPage_SmoothingSlider_QuantisesToHundredthsInConfig()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        var slider = All<Slider>(page, "SmoothingSlider")[0];

        slider.Value = 0.876;

        Assert.Equal(0.88, rig.Config.Config.Sliders[0].SmoothingFactor);
        Assert.Equal(0.88, slider.Value, 6);
    }
}
