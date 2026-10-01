using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.ViewModels;
using Volco.App.Views.Pages;
using Volco.Core.Audio;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.App.Tests.Pages;

public class OutputDevicesPageTests
{
    private static (OutputDevicesPage Page, TestRig Rig, SettingsViewModel Vm, FakeAudioEngine Engine) Show()
    {
        var engine = new FakeAudioEngine
        {
            Devices =
            {
                new AudioDevice("dev-1", "Speakers", AudioFlow.Render, true),
                new AudioDevice("dev-2", "Headphones", AudioFlow.Render, false),
                new AudioDevice("mic-1", "Microphone", AudioFlow.Capture, true),
            },
        };
        var rig = new TestRig(engine);
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new OutputDevicesPage(vm.OutputDevices);
        new Window { Content = page }.Show();
        Dispatcher.UIThread.RunJobs();
        return (page, rig, vm, engine);
    }

    private static ComboBox Combo(OutputDevicesPage page, string name)
        => page.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == name);

    private static string[] Items(ComboBox c)
        => Assert.IsAssignableFrom<IEnumerable<string>>(c.ItemsSource).ToArray();

    [AvaloniaFact]
    public void OutputDevicesPage_PickersListRenderDevicesOnly()
    {
        var (page, rig, vm, _) = Show();
        using var _ = rig; using var __ = vm;

        foreach (var name in new[] { "Device1Combo", "Device2Combo" })
            Assert.Equal(new[] { "Speakers", "Headphones" }, Items(Combo(page, name)));

        // Picking writes through to the config (id and name).
        Combo(page, "Device1Combo").SelectedItem = "Headphones";
        Assert.Equal("dev-2", rig.Config.Config.OutputDevices.Device1.Id);
        Assert.Equal("Headphones", rig.Config.Config.OutputDevices.Device1.FriendlyName);
    }

    [AvaloniaFact]
    public void OutputDevicesPage_DeviceAddedEvent_AppearsInPickers()
    {
        var (page, rig, vm, engine) = Show();
        using var _ = rig; using var __ = vm;

        var added = new AudioDevice("dev-3", "USB DAC", AudioFlow.Render, false);
        engine.Devices.Add(added);
        engine.RaiseDeviceChanged(DeviceChangeKind.Added, added);
        Dispatcher.UIThread.RunJobs();

        foreach (var name in new[] { "Device1Combo", "Device2Combo" })
            Assert.Contains("USB DAC", Items(Combo(page, name)));
    }
}
