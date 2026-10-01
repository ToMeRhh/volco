using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Volco.Core.Diagnostics;

namespace Volco.App;

public partial class VolcoApplication : Application
{
    private static volatile bool _isExiting;
    private static CompositionRoot? _root;

    /// <summary>Set by <see cref="VolcoAppHost.Run"/> before the framework starts.</summary>
    internal static Func<PlatformServices>? CreatePlatform { get; set; }

    /// Replaces WinUI's App.RequestExit/App.IsExiting (used by UpdateWindow and SettingsWindow).
    public static bool IsExiting => _isExiting;

    public static void RequestExit()
    {
        _isExiting = true;
        _root?.Shutdown();
        (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
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
                throw;
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
