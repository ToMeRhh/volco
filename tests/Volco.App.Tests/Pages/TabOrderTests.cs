using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.Controls;
using Volco.App.Views.Pages;
using Volco.App.Views.Stage;
using Xunit;

namespace Volco.App.Tests.Pages;

/// <summary>
/// Keyboard-only use. Tab walks the Controls page in reading order: each slider tile, the mode
/// tabs, Add mode and the edit pencil, each button tile, then the selected control's inspector.
/// </summary>
public class TabOrderTests
{
    private static List<string> TabSequence(Window window, Control start, int count)
    {
        start.Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();
        var names = new List<string>();
        for (int i = 0; i < count; i++)
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Dispatcher.UIThread.RunJobs();
            var focused = window.FocusManager?.GetFocusedElement() as Control;
            names.Add(focused?.Name ?? focused?.GetType().Name ?? "(none)");
        }
        return names;
    }

    [AvaloniaFact]
    public void StagePage_TabGoesTileByTile_ThenTheInspector()
    {
        using var rig = new TestRig();
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new StagePage(vm.Controls);
        var window = new Window { Content = page, Width = 932, Height = 680 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var first = page.GetVisualDescendants().OfType<SliderTile>().First();
        Assert.Equal(
            new[]
            {
                "SliderTile", "SliderTile", "SliderTile", "SliderTile",
                "ModeTab", "AddModeButton", "EditModesButton",
                "ButtonTile", "ButtonTile", "ButtonTile",
                "NameBox",
            },
            TabSequence(window, first, 11));
    }
}
