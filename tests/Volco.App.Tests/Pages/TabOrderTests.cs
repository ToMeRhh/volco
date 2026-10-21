using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.Controls;
using Volco.App.Views.Hardware;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.App.Views.Stage;
using Xunit;

namespace Volco.App.Tests.Pages;

/// <summary>
/// Keyboard-only use. Tab walks the Controls page in reading order: each slider tile, the mode
/// tabs, Add mode and the edit pencil, each button tile, then the selected control's inspector.
/// On Hardware it starts at the title row's buttons, and the mode tabs (faded out) are skipped.
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

    [AvaloniaFact]
    public void HardwareStage_TabGoesThroughTheTitleRow_ThenTileByTile()
    {
        var blob = ControllerConfigCodec.Encode(ControllerPresets.ApplyTo(new Volco.Core.Controller.Proto.ControllerConfig(),
            ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId)));
        using var rig = new TestRig();
        var c = rig.Config.Config;
        c.Sliders = Enumerable.Range(0, 5).Select(i => new SliderConfig { Index = i, Type = SliderMappingType.None }).ToList();
        c.Buttons = Enumerable.Range(0, 3).Select(i => new ButtonConfig { Index = i }).ToList();
        c.ControllerConfigBlob = Convert.ToBase64String(blob);
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new StagePage(vm.Controls)
        {
            Hardware = vm.Hardware,
            HardwareSidePanel = new HardwareInspectorHost { DataContext = vm.Hardware },
        };
        var window = new Window { Content = page, Width = 1100, Height = 720 };
        window.Show();
        rig.Connection!.SetStatus(TransportStatus.Open);
        rig.Connection.RaiseMessage(new HelloMessage(ProtocolVersion.Current, ChipFamily.Esp32, "dev", ControllerConfigState.Stored,
            ControllerConfigCodec.Crc32(blob), 5, new[] { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair }));
        page.Mode = StageMode.Hardware;
        for (int i = 0; i < 100 && !(vm.Controller.CanEdit && vm.Hardware.CanStartTest); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var add = page.GetVisualDescendants().OfType<Button>().First(b => b.Name == "AddButton");
        Assert.Equal(
            new[]
            {
                "ControllerSettingsChip", "TestButton",
                "SliderTile", "SliderTile", "SliderTile", "SliderTile", "SliderTile",
                "ButtonTile", "ButtonTile", "ButtonTile",
            },
            TabSequence(window, add, 10));
        window.Close();
    }
}
