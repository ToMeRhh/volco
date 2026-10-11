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
    public void GeneralPage_LinksToTheRepository()
    {
        using var rig = new TestRig();
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new GeneralPage(vm.General);
        new Window { Content = page }.Show();

        var link = page.FindControl<HyperlinkButton>("RepoLink")!;
        Assert.Equal(new Uri("https://github.com/ToMeRhh/volco"), link.NavigateUri);
        Assert.True(link.IsEffectivelyVisible);
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
    public void GeneralPage_DevBuild_ShowsTheCommitAndBuildDetails()
    {
        using var rig = new TestRig();
        var page = new GeneralPage(new GeneralViewModel(rig.Config, isRelease: false));
        new Window { Content = page }.Show();

        Assert.Equal($"Volco dev {BuildInfo.Commit}", page.FindControl<TextBlock>("VersionText")!.Text);
        var details = page.FindControl<TextBlock>("BuildDetailsText")!;
        Assert.Equal(BuildInfo.BuildDetails, details.Text);
        Assert.Contains("Branch ", details.Text);
        Assert.True(details.IsVisible);
    }

    [AvaloniaFact]
    public void GeneralPage_ReleaseBuild_ShowsJustTheVersion()
    {
        using var rig = new TestRig();
        var page = new GeneralPage(new GeneralViewModel(rig.Config, isRelease: true));
        new Window { Content = page }.Show();

        Assert.Equal($"Volco v{BuildInfo.Version}", page.FindControl<TextBlock>("VersionText")!.Text);
        Assert.False(page.FindControl<TextBlock>("BuildDetailsText")!.IsVisible);
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

    [AvaloniaFact]
    public void TheThirdPartyNotices_LoadWhenOpened()
    {
        using var rig = new TestRig();
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new GeneralPage(vm.General);
        new Window { Content = page }.Show();
        var box = page.FindControl<TextBox>("NoticesBox")!;
        Assert.True(string.IsNullOrEmpty(box.Text));
        page.FindControl<Expander>("NoticesExpander")!.IsExpanded = true;
        Assert.Contains("nanopb", box.Text);
        Assert.Contains("GNU LESSER GENERAL PUBLIC LICENSE", box.Text);
        Assert.Contains("SIL OPEN FONT LICENSE", box.Text!.ToUpperInvariant());
    }
}
