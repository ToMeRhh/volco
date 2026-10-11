using Avalonia;
using Avalonia.Controls;
using Volco.Core.Diagnostics;

namespace Volco.App;

public static class VolcoAppHost
{
    /// Entry point for every OS head. usePlatform adds the head's Avalonia windowing and rendering
    /// backends (Volco.App references no backend, so a head ships only its own). createPlatform
    /// runs once on the UI thread during framework initialisation. Returns the process exit code.
    public static int Run(string[] args, Func<AppBuilder, AppBuilder> usePlatform, Func<PlatformServices> createPlatform)
    {
        ArgumentNullException.ThrowIfNull(usePlatform);
        ArgumentNullException.ThrowIfNull(createPlatform);
        var launch = LaunchOptions.Parse(args);

        // One Volco per user: a second start shows the running one's window (or, started in the
        // background, does nothing) and exits before any UI or audio comes up.
        using var instance = SingleInstance.Claim(SingleInstance.DefaultName, showExisting: !launch.Background);
        if (instance is null)
        {
            AppLog.Flush();
            return 0;
        }

        VolcoApplication.CreatePlatform = createPlatform;
        VolcoApplication.Launch = launch;
        VolcoApplication.Instance = instance;
        return usePlatform(BuildAvaloniaApp()).StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    /// Configured builder without Avalonia backends or a platform factory.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<VolcoApplication>();
}
