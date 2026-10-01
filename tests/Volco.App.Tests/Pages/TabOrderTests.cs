using Avalonia.Controls;
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
    private static List<string?> TabSequence(Control start, int count)
    {
        var names = new List<string?>();
        IInputElement? current = start;
        for (int i = 0; i < count && current is not null; i++)
        {
            current = KeyboardNavigationHandler.GetNext(current, NavigationDirection.Next);
            names.Add((current as Control)?.Name);
        }
        return names;
    }

    [AvaloniaFact]
    public void SlidersPage_TabGoesCardByCard()
    {
        using var rig = new TestRig();
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new SlidersPage(vm.Sliders);
        new Window { Content = page, Width = 900, Height = 900 }.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            new[] { "Grip", "ExpandButton", "MenuButton", "Grip", "ExpandButton", "MenuButton" },
            TabSequence(page.FindControl<Button>("AddButton")!, 6));
    }

    [AvaloniaFact]
    public void ButtonsPage_TabGoesPresetsThenCardByCard()
    {
        using var rig = new TestRig();
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new ButtonsPage(vm.Buttons);
        new Window { Content = page, Width = 900, Height = 900 }.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            new[] { "AddFirstPresetButton", "Grip", "ExpandButton", "MenuButton", "Grip" },
            TabSequence(page.FindControl<Button>("AddButton")!, 5));
    }
}
