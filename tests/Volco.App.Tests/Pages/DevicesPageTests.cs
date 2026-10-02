using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.ViewModels;
using Volco.App.Views.Pages;
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.App.Tests.Pages;

public class DevicesPageTests
{
    private static FakeAudioEngine Engine()
    {
        var speakers = new AudioDevice("spk", "Speakers (Realtek High Definition Audio)", AudioFlow.Render, true, DeviceFormFactor.Speakers);
        var mic = new AudioDevice("mic", "Microphone (Yeti Stereo Microphone)", AudioFlow.Capture, true, DeviceFormFactor.Microphone);
        return new FakeAudioEngine
        {
            Devices = { speakers, new AudioDevice("hs", "Headset Earphone (Arctis Nova 7)", AudioFlow.Render, false, DeviceFormFactor.Headset), mic },
            DefaultRender = speakers, DefaultCapture = mic,
        };
    }

    private static (DevicesPage Page, TestRig Rig, SettingsViewModel Vm) Show(Action<AppConfig>? configure = null)
    {
        var rig = new TestRig(Engine());
        configure?.Invoke(rig.Config.Config);
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new DevicesPage(vm.Devices);
        new Window { Content = page, Width = 900, Height = 1000 }.Show();
        Dispatcher.UIThread.RunJobs();
        return (page, rig, vm);
    }

    private static List<T> All<T>(Control root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().Where(c => c.Name == name).ToList();

    [AvaloniaFact]
    public void ListsEnabledDevices_DefaultFirst_AndGroupsByFlow()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        Assert.Equal(new[] { "Speakers", "Headset Earphone" }, vm.Devices.Outputs.Select(d => d.DisplayName));
        Assert.Equal(new[] { "Microphone" }, vm.Devices.Inputs.Select(d => d.DisplayName));
        Assert.Equal("Default output", vm.Devices.Outputs[0].StatusText);
        Assert.Equal(3, All<Border>(page, "DeviceCard").Count);
    }

    [AvaloniaFact]
    public void DisconnectedDevices_AppearWhenCustomizedOrReferenced()
    {
        var (_, rig, vm) = Show(c =>
        {
            c.Devices.Add(new DeviceSettingsConfig { Id = "sony", FriendlyName = "Headphones (WH-1000XM4)", Nickname = "Sony" });
            c.Sliders[0].Type = SliderMappingType.Device;
            c.Sliders[0].Device = new DeviceRefConfig { Id = "tv", FriendlyName = "LG TV (NVIDIA)" };
        });
        using var _ = rig; using var __ = vm;
        var sony = vm.Devices.Outputs.Single(d => d.Id == "sony");
        var tv = vm.Devices.Outputs.Single(d => d.Id == "tv");
        Assert.Equal(("Sony", "Not connected", true), (sony.DisplayName, sony.StatusText, sony.CanForget));
        Assert.Equal(("LG TV", false, "Used by Slider 0"), (tv.DisplayName, tv.CanForget, tv.UsedBy));
    }

    [AvaloniaFact]
    public void Nickname_SavesAndUpdatesTitleAndPreview()
    {
        var (_, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        var spk = vm.Devices.Outputs[0];

        spk.Nickname = "Desk speakers";

        var entry = rig.Config.Config.FindDevice("spk")!;
        Assert.Equal("Desk speakers", entry.Nickname);
        Assert.Equal("Speakers (Realtek High Definition Audio)", entry.FriendlyName);
        Assert.Equal("Desk speakers", spk.DisplayName);
        Assert.Equal("Desk speakers", spk.Preview.Main);
        Assert.Equal("ACTIVE", spk.Preview.TopText);
        Assert.Null(entry.Icon);                                          // icon never chosen stays automatic
    }

    [AvaloniaFact]
    public void Icon_PickedFromTheTen_IsStored_ClearingBothRemovesTheEntry()
    {
        var (_, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        var spk = vm.Devices.Outputs[0];
        Assert.Equal(10, spk.IconChoices.Count);
        Assert.DoesNotContain(spk.IconChoices, c => c.Icon == ScreenIcon.Presets);
        Assert.Equal(ScreenIcon.Speaker, spk.Icon);                       // automatic, from the form factor

        spk.IconChoices.Single(c => c.Icon == ScreenIcon.Speakers).SelectCommand.Execute(null);
        Assert.Equal(ScreenIcon.Speakers, rig.Config.Config.FindDevice("spk")!.Icon);
        Assert.True(spk.IconChoices.Single(c => c.Icon == ScreenIcon.Speakers).IsSelected);

        spk.Nickname = "x";
        spk.Nickname = "";
        spk.ResetIconCommand.Execute(null);
        Assert.Null(rig.Config.Config.FindDevice("spk"));
    }

    [AvaloniaFact]
    public void Forget_RemovesADisconnectedDevice()
    {
        var (_, rig, vm) = Show(c => c.Devices.Add(new DeviceSettingsConfig { Id = "sony", FriendlyName = "Sony", Nickname = "S" }));
        using var _ = rig; using var __ = vm;
        vm.Devices.Outputs.Single(d => d.Id == "sony").ForgetCommand.Execute(null);
        Assert.Null(rig.Config.Config.FindDevice("sony"));
        Assert.DoesNotContain(vm.Devices.Outputs, d => d.Id == "sony");
    }

    [AvaloniaFact]
    public void HebrewNickname_TypesRightToLeft()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        var spk = vm.Devices.Outputs[0];
        spk.IsExpanded = true;
        spk.Nickname = "רמקולים";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(FlowDirection.RightToLeft, All<TextBox>(page, "NicknameBox")[0].FlowDirection);
    }

    [AvaloniaFact]
    public void DeviceChanges_RefreshTheLists()
    {
        var (_, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        var dock = new AudioDevice("dock", "Dock Speakers", AudioFlow.Render, false);
        rig.Engine.Devices.Add(dock);
        rig.Engine.RaiseDeviceChanged(DeviceChangeKind.Added, dock);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(vm.Devices.Outputs, d => d.Id == "dock");
    }

    [AvaloniaFact]
    public void ShowingThePage_RefreshesStaleReferences()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        var window = (Window)page.GetVisualRoot()!;
        Assert.DoesNotContain(vm.Devices.Outputs, d => d.Id == "tv");

        rig.Config.Config.Sliders[1].Type = SliderMappingType.Device;
        rig.Config.Config.Sliders[1].Device = new DeviceRefConfig { Id = "tv", FriendlyName = "LG TV (NVIDIA)" };
        window.Content = null;
        Dispatcher.UIThread.RunJobs();
        window.Content = page;
        Dispatcher.UIThread.RunJobs();

        var tv = vm.Devices.Outputs.Single(d => d.Id == "tv");
        Assert.Equal("Used by Slider 1", tv.UsedBy);
        Assert.False(tv.CanForget);
    }
}
