using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Volco.App.Tests.Shell;
using Volco.App.ViewModels;
using Volco.App.Views.Pages;
using Volco.Core.Diagnostics;
using Xunit;

namespace Volco.App.Tests.Pages;

/// <summary>Touches the process-global AppLog.IsDebugEnabled, so it must not run in parallel.</summary>
[CollectionDefinition("AppLogGlobalState", DisableParallelization = true)]
public class AppLogGlobalStateCollection;

[Collection("AppLogGlobalState")]
public class GeneralPageTests
{
    [AvaloniaFact]
    public void GeneralPage_DebugToggle_WritesConfigAndAppLog()
    {
        using var rig = new TestRig();
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new GeneralPage(vm.General);
        new Window { Content = page }.Show();
        var toggle = page.FindControl<ToggleSwitch>("DebugToggle")!;
        try
        {
            Assert.False(rig.Config.Config.DebugLogging);

            toggle.IsChecked = true;

            Assert.True(rig.Config.Config.DebugLogging);
            Assert.True(AppLog.IsDebugEnabled);

            toggle.IsChecked = false;

            Assert.False(rig.Config.Config.DebugLogging);
            Assert.False(AppLog.IsDebugEnabled);
        }
        finally { AppLog.IsDebugEnabled = false; }
    }

    [AvaloniaFact]
    public void GeneralPage_ShowsTheLogPath_AndAnOpenFolderButton()
    {
        using var rig = new TestRig();
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new GeneralPage(vm.General);
        new Window { Content = page }.Show();

        Assert.Equal(AppLog.LogPath, page.FindControl<TextBlock>("LogPathText")!.Text);
        Assert.NotNull(page.FindControl<Button>("OpenLogFolderButton")!.Command);
    }

    [AvaloniaFact]
    public void GeneralPage_ShowsWhereTheSettingsAreSaved()
    {
        using var rig = new TestRig();
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new GeneralPage(vm.General);
        new Window { Content = page }.Show();

        var path = page.FindControl<TextBlock>("ConfigPathText")!;
        Assert.Equal(rig.Config.ConfigPath, path.Text);
        Assert.EndsWith("config.json", path.Text);
        Assert.True(path.IsEffectivelyVisible);
        Assert.NotNull(page.FindControl<Button>("OpenConfigFolderButton")!.Command);
    }

    [AvaloniaFact]
    public void GeneralPage_NoLongerHoldsTheAboutDetails()
    {
        using var rig = new TestRig();
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new GeneralPage(vm.General);
        new Window { Content = page }.Show();

        Assert.Null(page.FindControl<Border>("AboutPanel"));
        Assert.Null(page.FindControl<HyperlinkButton>("RepoLink"));
    }

    [AvaloniaFact]
    public void GeneralPage_StartAtSignIn_HiddenWithoutThePlatformService()
    {
        using var rig = new TestRig();
        var page = new GeneralPage(new GeneralViewModel(rig.Config, isRelease: false));
        new Window { Content = page }.Show();

        Assert.False(page.FindControl<Border>("StartupPanel")!.IsVisible);
    }

    [AvaloniaFact]
    public void GeneralPage_StartAtSignIn_TogglesTheOsSetting()
    {
        using var rig = new TestRig();
        var startup = new FakeStartWithSystem();
        var vm = new GeneralViewModel(rig.Config, isRelease: false, startup);
        var page = new GeneralPage(vm);
        new Window { Content = page }.Show();
        var toggle = page.FindControl<ToggleSwitch>("StartAtSignInToggle")!;
        Assert.True(page.FindControl<Border>("StartupPanel")!.IsVisible);
        Assert.False(toggle.IsChecked);

        toggle.IsChecked = true;
        Assert.Equal(new[] { true }, startup.Calls);
        Assert.True(startup.IsEnabled);

        toggle.IsChecked = false;
        Assert.Equal(new[] { true, false }, startup.Calls);
        Assert.False(startup.IsEnabled);
        Assert.False(rig.Config.Config.DebugLogging);   // nothing of it is kept in Volco's config
    }

    [AvaloniaFact]
    public void GeneralPage_StartAtSignIn_ARefusedChangeFlipsBackAndSaysWhy()
    {
        using var rig = new TestRig();
        var startup = new FakeStartWithSystem { Refuse = true };
        var vm = new GeneralViewModel(rig.Config, isRelease: false, startup);
        var page = new GeneralPage(vm);
        new Window { Content = page }.Show();

        vm.StartAtSignIn = true;

        Assert.False(vm.StartAtSignIn);
        Assert.False(page.FindControl<ToggleSwitch>("StartAtSignInToggle")!.IsChecked);
        Assert.Equal("Couldn't change this: Access is denied.", vm.StartAtSignInError);
        Assert.True(page.FindControl<TextBlock>("StartAtSignInErrorText")!.IsVisible);
    }

    [AvaloniaFact]
    public void GeneralPage_StartAtSignIn_ReReadsTheOsWhenShown()
    {
        using var rig = new TestRig();
        var startup = new FakeStartWithSystem();
        var vm = new GeneralViewModel(rig.Config, isRelease: false, startup);
        var page = new GeneralPage(vm);
        var window = new Window { Content = page };
        window.Show();
        Assert.False(vm.StartAtSignIn);

        window.Content = null;
        startup.IsEnabled = true;   // turned on in the OS's own startup settings
        window.Content = page;

        Assert.True(vm.StartAtSignIn);
        Assert.Empty(startup.Calls);
    }

    [AvaloniaFact]
    public void GeneralPage_UpdatesPanel_OnlyWhenUpdatesAreShown()
    {
        using var rig = new TestRig();

        var withUpdates = new GeneralPage(new GeneralViewModel(rig.Config, isRelease: true));
        var without = new GeneralPage(new GeneralViewModel(rig.Config, isRelease: false));

        Assert.NotNull(withUpdates.Updates);
        Assert.Same(withUpdates.Updates, withUpdates.FindControl<ContentControl>("UpdatesHost")!.Content);
        Assert.Null(without.Updates);
        Assert.Null(without.FindControl<ContentControl>("UpdatesHost")!.Content);
    }
}
