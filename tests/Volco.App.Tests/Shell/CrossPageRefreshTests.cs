using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.Controls;
using Volco.App.ViewModels;
using Volco.App.Views;
using Volco.App.Views.Pages;
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.App.Tests.Shell;

/// <summary>Pages and their view models are cached by the settings window, so an edit on one page
/// must reach the others that are already built.</summary>
public class CrossPageRefreshTests
{
    private static readonly DeviceRefConfig Speakers = new() { Id = "dev-1", FriendlyName = "Speakers" };
    private static readonly DeviceRefConfig Headphones = new() { Id = "dev-2", FriendlyName = "Headphones" };

    private static (SettingsWindow Window, TestRig Rig, SettingsViewModel Vm) Open(Action<AppConfig>? configure = null)
    {
        var speakers = new AudioDevice("dev-1", "Speakers", AudioFlow.Render, true);
        var engine = new FakeAudioEngine
        {
            Devices = { speakers, new AudioDevice("dev-2", "Headphones", AudioFlow.Render, false) },
            DefaultRender = speakers,
        };
        var rig = new TestRig(engine);
        configure?.Invoke(rig.Config.Config);
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var window = new SettingsWindow(vm, rig.Runtime) { Width = 1000, Height = 1000 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, rig, vm);
    }

    private static void Show(SettingsWindow window, string tag)
    {
        window.ShowPage(tag);
        Dispatcher.UIThread.RunJobs();
    }

    private static T In<T>(Control root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [AvaloniaFact]
    public void NicknameSetOnDevices_ReachesTheOpenButtonsPage()
    {
        var (window, rig, vm) = Open(c =>
        {
            c.Buttons[0].Default().SinglePress = new ButtonActionConfig
            {
                Action = ButtonActionType.ToggleOutputDevice, Device = Speakers, DeviceB = Headphones,
            };
            c.Buttons[1].Default().SinglePress = new ButtonActionConfig
            {
                Action = ButtonActionType.MuteSpecificDevice, Device = Headphones,
            };
        });
        using var _ = rig; using var __ = vm;
        Show(window, "buttons");
        vm.Buttons.Items[1].IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Toggle Speakers / Headphones", vm.Buttons.Items[0].Summary);

        Show(window, "devices");
        vm.Devices.Outputs.Single(d => d.Id == "dev-2").Nickname = "Arctis";
        Dispatcher.UIThread.RunJobs();
        Show(window, "buttons");

        Assert.Equal("Toggle Speakers / Arctis", vm.Buttons.Items[0].Summary);
        Assert.Equal("Arctis", vm.Buttons.Items[1].Screen.Main);   // closed-card / card screen preview
        var page = Assert.IsType<ButtonsPage>(window.CurrentPage);
        var card = page.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "Card").ElementAt(1);
        var combo = In<ComboBox>(In<ActionRow>(card, "PressRow"), "DeviceCombo");
        Assert.Equal("Headphones", combo.SelectedItem);
        Assert.Equal("Arctis", In<TextBlock>(combo, "DeviceLabel").Text);
        Assert.Equal("Headphones", In<TextBlock>(combo, "DeviceSecondary").Text);
    }

    [AvaloniaFact]
    public void NicknameSetOnDevices_ReachesTheOpenSlidersPicker()
    {
        var (window, rig, vm) = Open(c =>
        {
            c.Sliders[0].Type = SliderMappingType.Device;
            c.Sliders[0].Device = Headphones;
        });
        using var _ = rig; using var __ = vm;
        vm.Sliders.Items[0].IsExpanded = true;
        Dispatcher.UIThread.RunJobs();

        Show(window, "devices");
        vm.Devices.Outputs.Single(d => d.Id == "dev-2").Nickname = "Arctis";
        Dispatcher.UIThread.RunJobs();
        Show(window, "sliders");

        var combo = In<ComboBox>(Assert.IsType<SlidersPage>(window.CurrentPage), "DeviceCombo");
        Assert.Equal("Headphones", combo.SelectedItem);
        Assert.Equal("Arctis", In<TextBlock>(combo, "DeviceLabel").Text);
        Assert.Equal(Headphones.Id, rig.Config.Config.Sliders[0].Device?.Id);   // the re-seed saved nothing new
    }

    [AvaloniaFact]
    public void ActionEditedOnButtons_UpdatesUsedByWhenDevicesIsShownAgain()
    {
        var (window, rig, vm) = Open();
        using var _ = rig; using var __ = vm;
        Show(window, "devices");
        var headphones = vm.Devices.Outputs.Single(d => d.Id == "dev-2");
        Assert.Equal("", headphones.UsedBy);
        var changed = new List<string?>();
        headphones.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Show(window, "buttons");
        var slot = vm.Buttons.Items[2].Press;
        slot.SelectedChoice = ActionCatalog.For(ButtonActionType.MuteSpecificDevice);
        slot.Device = "Headphones";
        Dispatcher.UIThread.RunJobs();
        Show(window, "devices");

        Assert.Same(headphones, vm.Devices.Outputs.Single(d => d.Id == "dev-2"));   // card kept
        Assert.Equal("Used by Button 2", headphones.UsedBy);
        Assert.Contains(nameof(DeviceItemViewModel.UsedBy), changed);
        Assert.Contains(nameof(DeviceItemViewModel.ShowUsedBy), changed);
    }
}
