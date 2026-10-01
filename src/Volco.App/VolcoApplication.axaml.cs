using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Volco.Core.Diagnostics;

namespace Volco.App;

public partial class VolcoApplication : Application
{
    private static volatile bool _isExiting;
    private static CompositionRoot? _root;

    /// <summary>Set by <see cref="VolcoAppHost.Run"/> before the framework starts.</summary>
    internal static Func<PlatformServices>? CreatePlatform { get; set; }

    /// Replaces WinUI's App.RequestExit/App.IsExiting (used by the Updates panel and SettingsWindow).
    public static bool IsExiting => _isExiting;

    public static void RequestExit()
    {
        _isExiting = true;
        _root?.Shutdown();
        (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    /// <summary>
    /// OS logoff/shutdown: run the same teardown as <see cref="RequestExit"/> so the runtime and
    /// engine dispose cleanly. The lifetime is already shutting down, so it is not asked again.
    /// </summary>
    internal static void OnSessionEnding()
    {
        _isExiting = true;
        _root?.Shutdown();
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += (_, _) =>
            {
                AppLog.Info("Shutdown requested by the OS or platform");
                OnSessionEnding();
            };
            // Replaces the WinUI App constructor's UnhandledException handler: log and keep running.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                AppLog.Error($"Unhandled UI-thread exception: {e.Exception}");
                e.Handled = true;
            };
            try
            {
                var factory = CreatePlatform
                    ?? throw new InvalidOperationException("Start through VolcoAppHost.Run.");
                var configPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Volco", "config.json");
                _root = new CompositionRoot(configPath, factory);
                _root.Start();
            }
            catch (Exception ex)
            {
                AppLog.Error($"Startup failed: {ex}");
                AppLog.Flush();
                throw;
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
