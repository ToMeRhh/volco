using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.Controls;
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

    private static T InRow<T>(ButtonsPage page, string slot, string name, int button = 0) where T : Control
        => All<PressActionRow>(page, slot + "PressRow")[button]
            .GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public void ButtonsPage_RendersOneCardPerButton_WithThreePressRows()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        int buttons = rig.Config.Config.Buttons.Count;

        Assert.True(buttons > 0);
        Assert.Equal(buttons, All<Border>(page, "ButtonCard").Count);
        Assert.Equal(buttons, All<PressActionRow>(page, "SinglePressRow").Count);
        Assert.Equal(buttons, All<PressActionRow>(page, "DoublePressRow").Count);
        Assert.Equal(buttons, All<PressActionRow>(page, "LongPressRow").Count);
        Assert.Equal(new[] { "Single Press", "Double Press", "Long Press" },
            new[] { "Single", "Double", "Long" }
                .Select(k => All<PressActionRow>(page, k + "PressRow")[0].Label).ToArray());
        Assert.Equal(buttons, All<NumericUpDown>(page, "LongPressBox").Count);
        Assert.Equal(buttons, All<NumericUpDown>(page, "DoublePressBox").Count);
    }

    [AvaloniaFact]
    public void ButtonsPage_ActionNeedingDevice_ShowsDevicePicker()
    {
        var (page, rig, vm) = Show(c => c.Buttons[0].SinglePress.Action = ButtonActionType.MuteSpecificDevice);
        using var _ = rig; using var __ = vm;

        Assert.True(InRow<ComboBox>(page, "Single", "DeviceCombo").IsVisible);
        // The other slots still have no action, so no picker.
        Assert.False(InRow<ComboBox>(page, "Double", "DeviceCombo").IsVisible);
        Assert.False(InRow<ComboBox>(page, "Long", "DeviceCombo").IsVisible);
    }

    [AvaloniaFact]
    public void ButtonsPage_MuteCurrentOutput_HidesDevicePicker()
    {
        var (page, rig, vm) = Show(c => c.Buttons[0].SinglePress.Action = ButtonActionType.MuteCurrentOutput);
        using var _ = rig; using var __ = vm;
        var picker = InRow<ComboBox>(page, "Single", "DeviceCombo");
        Assert.False(picker.IsVisible);

        // Switching to a device-taking action reveals it.
        InRow<ComboBox>(page, "Single", "ActionCombo").SelectedIndex = 5;
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
        var picker = InRow<ComboBox>(page, "Single", "DeviceCombo");

        Assert.True(picker.IsVisible);
        Assert.Equal("Speakers", picker.SelectedItem);
        Assert.True(picker.SelectedIndex >= 0);
        Assert.Contains("Speakers", Assert.IsAssignableFrom<IEnumerable<string>>(picker.ItemsSource));
    }

    [AvaloniaFact]
    public void ButtonsPage_ChangeAction_PersistsToConfig()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        Assert.Equal(ButtonActionType.None, rig.Config.Config.Buttons[0].LongPress.Action);

        InRow<ComboBox>(page, "Long", "ActionCombo").SelectedIndex = 2;
        Assert.Equal(ButtonActionType.MuteCurrentOutput, rig.Config.Config.Buttons[0].LongPress.Action);

        All<NumericUpDown>(page, "LongPressBox")[0].Value = 800;
        Assert.Equal(800, rig.Config.Config.Buttons[0].LongPressMs);
    }

    [AvaloniaFact]
    public void ButtonsPage_ChoosingDevice_PersistsIdAndNameToConfig()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;

        InRow<ComboBox>(page, "Single", "ActionCombo").SelectedIndex = 5;
        InRow<ComboBox>(page, "Single", "DeviceCombo").SelectedItem = "Headphones";

        var cfg = rig.Config.Config.Buttons[0].SinglePress;
        Assert.Equal(ButtonActionType.SetOutputDevice, cfg.Action);
        Assert.Equal("dev-2", cfg.Device!.Id);
        Assert.Equal("Headphones", cfg.Device.FriendlyName);
    }

    [AvaloniaFact]
    public void ButtonsPage_ExpandButton_TogglesBody()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        var body = All<StackPanel>(page, "Body")[0];
        Assert.True(body.IsVisible);

        var button = All<Button>(page, "ExpandButton")[0];
        button.Command!.Execute(button.CommandParameter);
        Assert.False(body.IsVisible);

        button.Command!.Execute(button.CommandParameter);
        Assert.True(body.IsVisible);
    }
}
