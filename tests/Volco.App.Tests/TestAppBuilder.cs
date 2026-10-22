using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Volco.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]
// Headless Avalonia tests share one UI thread, and AppLog.IsDebugEnabled is process-wide, so
// test classes run one at a time.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Volco.App.Tests;

public class TestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        // Headless tests don't run animations to their end, so the settings window's crossfades
        // swap at once (as with Windows animations off); a test of a crossfade turns them on.
        Volco.App.Views.Stage.StageTransitions.Enabled = false;
        // Likewise a confirmed save's progress dialog closes at once, so a test can go on clicking
        // the page; a test of the "Saved." hold sets its own.
        Volco.App.ViewModels.Hardware.ProgressDialogViewModel.SavedHold = TimeSpan.Zero;
        Resources["ScreenIconToGeometry"] = new Volco.App.Converters.ScreenIconToGeometryConverter();
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Volco.App/"))
        {
            Source = new Uri("avares://Volco.App/Styles/VolcoStyles.axaml")
        });
    }
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
