using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.ViewModels;
using Volco.App.Views.Pages;
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.App.Tests.Pages;

public class ButtonsPageTests
{
    private static FakeAudioEngine EngineWithAudio() => new()
    {
        Devices =
        {
            new AudioDevice("dev-1", "Speakers", AudioFlow.Render, true),
            new AudioDevice("dev-2", "Headphones", AudioFlow.Render, false),
        },
    };

    private static (ButtonsPage Page, TestRig Rig, SettingsViewModel Vm) Show(Action<AppConfig>? configure = null)
    {
        var rig = new TestRig(EngineWithAudio());
        configure?.Invoke(rig.Config.Config);
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new ButtonsPage(vm.Buttons);
        new Window { Content = page }.Show();
        Dispatcher.UIThread.RunJobs();
        return (page, rig, vm);
    }

    private static List<T> All<T>(ButtonsPage page, string name) where T : Control
        => page.GetVisualDescendants().OfType<T>().Where(c => c.Name == name).ToList();

    [AvaloniaFact]
    public void ButtonsPage_RendersOneCardPerButton_WithThreePressRows()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        int buttons = rig.Config.Config.Buttons.Count;

        Assert.True(buttons > 0);
        Assert.Equal(buttons, All<Border>(page, "ButtonCard").Count);
        Assert.Equal(buttons, All<ComboBox>(page, "SingleActionCombo").Count);
        Assert.Equal(buttons, All<ComboBox>(page, "DoubleActionCombo").Count);
        Assert.Equal(buttons, All<ComboBox>(page, "LongActionCombo").Count);
        Assert.Equal(buttons, All<NumericUpDown>(page, "LongPressBox").Count);
        Assert.Equal(buttons, All<NumericUpDown>(page, "DoublePressBox").Count);
    }

    [AvaloniaFact]
    public void ButtonsPage_ActionNeedingDevice_ShowsDevicePicker()
    {
        var (page, rig, vm) = Show(c => c.Buttons[0].SinglePress.Action = ButtonActionType.MuteSpecificDevice);
        using var _ = rig; using var __ = vm;

        Assert.True(All<ComboBox>(page, "SingleDeviceCombo")[0].IsVisible);
        // The other slots still have no action, so no picker.
        Assert.False(All<ComboBox>(page, "DoubleDeviceCombo")[0].IsVisible);
        Assert.False(All<ComboBox>(page, "LongDeviceCombo")[0].IsVisible);
    }

    [AvaloniaFact]
    public void ButtonsPage_MuteCurrentOutput_HidesDevicePicker()
    {
        var (page, rig, vm) = Show(c => c.Buttons[0].SinglePress.Action = ButtonActionType.MuteCurrentOutput);
        using var _ = rig; using var __ = vm;
        var picker = All<ComboBox>(page, "SingleDeviceCombo")[0];
        Assert.False(picker.IsVisible);

        // Switching to a device-taking action reveals it.
        All<ComboBox>(page, "SingleActionCombo")[0].SelectedIndex = 5;
        Assert.True(picker.IsVisible);
    }

    [AvaloniaFact]
    public void ButtonsPage_FriendlyNameOnlyDevice_IsSelected()
    {
        var (page, rig, vm) = Show(c =>
        {
            c.Buttons[0].SinglePress.Action = ButtonActionType.SetOutputDevice;
            c.Buttons[0].SinglePress.Device = new DeviceRefConfig { Id = "", FriendlyName = "Speakers" };
        });
        using var _ = rig; using var __ = vm;
        var picker = All<ComboBox>(page, "SingleDeviceCombo")[0];

        Assert.True(picker.IsVisible);
        Assert.Equal("Speakers", picker.SelectedItem);
        Assert.Contains("Speakers", Assert.IsAssignableFrom<IEnumerable<string>>(picker.ItemsSource));
    }

    [AvaloniaFact]
    public void ButtonsPage_ChangeAction_PersistsToConfig()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        Assert.Equal(ButtonActionType.None, rig.Config.Config.Buttons[0].LongPress.Action);

        All<ComboBox>(page, "LongActionCombo")[0].SelectedIndex = 2;
        Assert.Equal(ButtonActionType.MuteCurrentOutput, rig.Config.Config.Buttons[0].LongPress.Action);

        All<NumericUpDown>(page, "LongPressBox")[0].Value = 800;
        Assert.Equal(800, rig.Config.Config.Buttons[0].LongPressMs);
    }
}
