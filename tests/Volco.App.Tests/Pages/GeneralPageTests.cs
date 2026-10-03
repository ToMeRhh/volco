using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Volco.App.ViewModels;
using Volco.App.Views.Pages;
using Volco.Core.Diagnostics;
using Volco.Core.Updates;
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
    public void GeneralPage_ShowsTheVersionAndBuildDetails()
    {
        using var rig = new TestRig();
        var page = new GeneralPage(new GeneralViewModel(rig.Config, showUpdates: false));
        new Window { Content = page }.Show();

        Assert.Equal($"Volco {BuildInfo.DisplayVersion}", page.FindControl<TextBlock>("VersionText")!.Text);
        var details = page.FindControl<TextBlock>("BuildDetailsText")!;
        Assert.Equal(BuildInfo.BuildDetails, details.Text);
        Assert.True(details.IsVisible);
    }

    [AvaloniaFact]
    public void GeneralPage_UpdatesPanel_OnlyWhenUpdatesAreShown()
    {
        using var rig = new TestRig();

        var withUpdates = new GeneralPage(new GeneralViewModel(rig.Config, showUpdates: true));
        var without = new GeneralPage(new GeneralViewModel(rig.Config, showUpdates: false));

        Assert.NotNull(withUpdates.Updates);
        Assert.Same(withUpdates.Updates, withUpdates.FindControl<ContentControl>("UpdatesHost")!.Content);
        Assert.Null(without.Updates);
        Assert.Null(without.FindControl<ContentControl>("UpdatesHost")!.Content);
    }
}
