using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Volco.App.Views.Pages;
using Xunit;

namespace Volco.App.Tests.Pages;

/// <summary>
/// Review Focus #3: keyboard-only use. Tab walks each page in reading order: the header's add
/// button, then each card's drag handle (reorder with Alt+Up/Down), its header (open/close) and
/// its ⋯ menu, card by card.
/// </summary>
public class TabOrderTests
{
    private static List<string?> TabSequence(Window window, Control start, int count)
    {
        start.Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();
        var names = new List<string?>();
        for (int i = 0; i < count; i++)
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Dispatcher.UIThread.RunJobs();
            names.Add((window.FocusManager?.GetFocusedElement() as Control)?.Name);
        }
        return names;
    }

    [AvaloniaFact]
    public void SlidersPage_TabGoesCardByCard()
    {
        using var rig = new TestRig();
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new SlidersPage(vm.Sliders);
        var window = new Window { Content = page, Width = 900, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            new[] { "Grip", "ExpandButton", "MenuButton", "Grip", "ExpandButton", "MenuButton" },
            TabSequence(window, page.FindControl<Button>("AddButton")!, 6));
    }

    [AvaloniaFact]
    public void ButtonsPage_TabGoesPresetsThenCardByCard()
    {
        using var rig = new TestRig();
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new ButtonsPage(vm.Buttons);
        var window = new Window { Content = page, Width = 900, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            new[] { "AddFirstPresetButton", "Grip", "ExpandButton", "MenuButton", "Grip" },
            TabSequence(window, page.FindControl<Button>("AddButton")!, 5));
    }
}
