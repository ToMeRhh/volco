using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    public void NicknameSetInTheDeviceDialog_ReachesTheControlsPage()
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
        Show(window, "controls");
        vm.Controls.Select(vm.Buttons.Items[1]);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Toggle Speakers / Headphones", vm.Buttons.Items[0].MainLabel);

        vm.Devices.ItemFor("dev-2", AudioFlow.Render)!.Nickname = "Arctis";   // the device dialog's field
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Toggle Speakers / Arctis", vm.Buttons.Items[0].MainLabel);
        Assert.Equal("Arctis", vm.Buttons.Items[1].Screen.Main);   // the screen preview
        var page = Assert.IsType<ControlsPage>(window.CurrentPage);
        var row = In<ContentControl>(page, "ActionRow_Press");
        Assert.Equal("Mute Arctis", In<TextBlock>(row, "ActionText").Text);
    }

    [AvaloniaFact]
    public void NicknameSetInTheDeviceDialog_ReachesTheSliderLabels()
    {
        var (window, rig, vm) = Open(c =>
        {
            c.Sliders[0].Type = SliderMappingType.Device;
            c.Sliders[0].Device = Headphones;
        });
        using var _ = rig; using var __ = vm;
        Show(window, "controls");
        var page = Assert.IsType<ControlsPage>(window.CurrentPage);
        Assert.Equal("Headphones", In<TextBlock>(page, "TargetText").Text);   // slider 1 is selected

        vm.Devices.ItemFor("dev-2", AudioFlow.Render)!.Nickname = "Arctis";   // the device dialog's field
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Arctis", In<TextBlock>(page, "TargetText").Text);
        Assert.Equal("Arctis", In<TextBlock>(page.GetVisualDescendants().OfType<Volco.App.Controls.SliderTile>().First(), "Label").Text);
        Assert.Equal("Arctis", In<TextBox>(page, "NameBox").PlaceholderText);
        Assert.Equal("Headphones", vm.Sliders.Items[0].SelectedDevice);
        Assert.Equal(Headphones.Id, rig.Config.Config.Sliders[0].Device?.Id);   // the re-seed saved nothing new
    }

    [AvaloniaFact]
    public void ActionEdited_UsedByIsCurrentWhenTheDeviceDialogOpens()
    {
        var (window, rig, vm) = Open();
        using var _ = rig; using var __ = vm;
        Show(window, "controls");
        var headphones = vm.Devices.ItemFor("dev-2", AudioFlow.Render)!;
        Assert.Equal("", headphones.UsedBy);
        var changed = new List<string?>();
        headphones.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        var slot = vm.Buttons.Items[2].Press;
        slot.SelectedChoice = ActionCatalog.For(ButtonActionType.MuteSpecificDevice);
        slot.Device = "Headphones";
        Dispatcher.UIThread.RunJobs();

        Assert.Same(headphones, vm.Devices.ItemFor("dev-2", AudioFlow.Render));   // the item is kept
        Assert.Equal("Used by: Button 3", headphones.UsedBy);
        Assert.Contains(nameof(DeviceItemViewModel.UsedBy), changed);
        Assert.Contains(nameof(DeviceItemViewModel.CanForget), changed);
    }
}
