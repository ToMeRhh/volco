using Avalonia;
using Avalonia.Controls;

namespace Volco.App;

public static class VolcoAppHost
{
    /// Entry point for every OS head. createPlatform runs once on the UI thread during
    /// framework initialisation. Returns the process exit code.
    public static int Run(string[] args, Func<PlatformServices> createPlatform)
    {
        ArgumentNullException.ThrowIfNull(createPlatform);
        VolcoApplication.CreatePlatform = createPlatform;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    /// Configured builder without a platform factory, for the previewer and tests.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<VolcoApplication>().UsePlatformDetect();
}
