using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Volco.App.ViewModels;
using Volco.App.Views;
using Volco.App.Views.Pages;
using Volco.Core.Config;
using Volco.Core.Runtime;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.App.Tests.Shell;

public class SettingsWindowTests
{
    private static SettingsWindow Create()
    {
        var dir = Path.Combine(Path.GetTempPath(), "volco-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = new ConfigManager(Path.Combine(dir, "config.json"));
        config.Load();
        var runtime = new VolcoRuntime(config, new FakeAudioEngine(), audioReady: true);
        var vm = new SettingsViewModel(config, runtime.Audio, runtime, new AvaloniaUiDispatcher());
        return new SettingsWindow(vm, runtime);
    }

    [AvaloniaFact]
    public void SettingsWindow_Close_HidesAndCanReopen()
    {
        var window = Create();
        window.Show();
        Assert.True(window.IsVisible);

        window.Close();

        Assert.False(window.IsVisible);

        window.Show();
        Assert.True(window.IsVisible);
        var host = window.FindControl<ContentControl>("PageHost")!;
        Assert.IsType<ConnectionPage>(host.Content);
    }

    [AvaloniaFact]
    public void SettingsWindow_NavSelection_SwapsPage()
    {
        var window = Create();
        window.Show();
        var nav = window.FindControl<ListBox>("NavList")!;
        var host = window.FindControl<ContentControl>("PageHost")!;

        var expected = new[]
        {
            typeof(ConnectionPage), typeof(SlidersPage), typeof(ButtonsPage),
            typeof(OutputDevicesPage), typeof(GeneralPage),
        };
        Assert.Equal(5, nav.ItemCount);
        for (var i = 0; i < expected.Length; i++)
        {
            nav.SelectedIndex = i;
            Assert.IsType(expected[i], host.Content);
        }
        Assert.Equal(
            new[] { "Connection", "Sliders", "Buttons", "Output Devices", "General" },
            nav.Items.Cast<NavItem>().Select(n => n.Label).ToArray());
    }

    [AvaloniaFact]
    public void SettingsWindow_MinSize_Is680x560()
    {
        var window = Create();
        Assert.Equal(680, window.MinWidth);
        Assert.Equal(560, window.MinHeight);
        Assert.Equal(720, window.Width);
        Assert.Equal(600, window.Height);
    }
}
