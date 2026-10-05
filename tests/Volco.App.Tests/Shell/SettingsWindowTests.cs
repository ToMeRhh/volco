using Avalonia.Controls;
using Avalonia.Controls.Chrome;
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
        Assert.IsType<SlidersPage>(host.Content);
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
            typeof(SlidersPage), typeof(ButtonsPage), typeof(DevicesPage), typeof(ConnectionPage), typeof(GeneralPage),
        };
        Assert.Equal(5, nav.ItemCount);
        for (var i = 0; i < expected.Length; i++)
        {
            nav.SelectedIndex = i;
            Assert.IsType(expected[i], host.Content);
        }
        Assert.Equal(
            new[] { "Sliders", "Buttons", "Devices", "Connection", "General" },
            nav.Items.Cast<NavItem>().Select(n => n.Label).ToArray());
    }

    [AvaloniaFact]
    public void SettingsWindow_ShowPage_SwitchesByTag()
    {
        var window = Create();
        window.Show();

        window.ShowPage("general");

        Assert.IsType<GeneralPage>(window.CurrentPage);
    }

    [AvaloniaFact]
    public void SettingsWindow_FooterShowsTheConnectionState()
    {
        var window = Create();
        window.Show();

        Assert.Equal("Not connected", window.FindControl<TextBlock>("FooterText")!.Text);
    }

    [AvaloniaFact]
    public void SettingsWindow_ExtendsIntoTheTitleBar_WithDragRegions()
    {
        var window = Create();
        window.Show();

        Assert.True(window.ExtendClientAreaToDecorationsHint);
        Assert.Equal(WindowDecorations.Full, window.WindowDecorations);
        foreach (var name in new[] { "SidebarTitleBar", "PageTitleBar" })
        {
            var region = window.FindControl<Border>(name)!;
            Assert.Equal("TitleBar", region.GetValue(WindowDecorationProperties.ElementRoleProperty).ToString());
        }
        Assert.Equal("Volco", window.FindControl<TextBlock>("BrandText")!.Text);
    }

    [AvaloniaFact]
    public void SettingsWindow_UndoMessage_ShowsAndUndoes()
    {
        var window = Create();
        window.Show();
        var vm = Assert.IsType<SettingsViewModel>(window.DataContext);
        var toast = window.FindControl<Border>("Toast")!;
        Assert.False(toast.IsVisible);
        var undone = false;

        vm.Toast.Show("Slider 2 reset", () => undone = true);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(toast.IsVisible);
        Assert.Equal("Slider 2 reset", window.FindControl<TextBlock>("ToastText")!.Text);

        window.FindControl<Button>("UndoButton")!.Command!.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(undone);
        Assert.False(toast.IsVisible);
    }

    [AvaloniaFact]
    public void SettingsWindow_Size()
    {
        var window = Create();
        Assert.Equal(680, window.MinWidth);
        Assert.Equal(520, window.MinHeight);
        Assert.Equal(860, window.Width);
        Assert.Equal(660, window.Height);
    }

    [Theory]
    [InlineData(WindowCloseReason.WindowClosing, false, false, true)]
    [InlineData(WindowCloseReason.Undefined, false, false, true)]
    [InlineData(WindowCloseReason.ApplicationShutdown, false, false, false)]
    [InlineData(WindowCloseReason.OSShutdown, false, false, false)]
    [InlineData(WindowCloseReason.WindowClosing, true, false, false)]
    [InlineData(WindowCloseReason.WindowClosing, false, true, false)]
    public void SettingsWindow_ShouldHideInsteadOfClose_HonoursShutdownReasons(
        WindowCloseReason reason, bool allowClose, bool isExiting, bool expectedHide)
    {
        Assert.Equal(expectedHide, SettingsWindow.ShouldHideInsteadOfClose(reason, allowClose, isExiting));
    }
}
