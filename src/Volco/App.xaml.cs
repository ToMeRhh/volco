using H.NotifyIcon;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Diagnostics;
using Volco.Core.Runtime;
using Volco.UI;
using Volco.Core.Updates;

namespace Volco;

public partial class WinUiApp : Application
{
    public static bool IsExiting { get; private set; }
    private static WinUiApp _instance = null!;
    private TaskbarIcon? _trayIcon;
    private SettingsWindow? _settingsWindow;
    private UpdateWindow? _updateWindow;
    private ConfigManager _configManager = null!;
    private IAudioEngine _audioEngine = null!;
    private VolcoRuntime _runtime = null!;
    private VolumeOsdWindow _osd = null!;

    public WinUiApp()
    {
        _instance = this;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            AppLog.Error($"Unhandled exception: {e.Exception}");
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppLog.Info("App launching");

        // Config
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Volco", "config.json");
        _configManager = new ConfigManager(configPath);
        _configManager.Load();

        // Audio first — VolcoRuntime's v1→v2 migration needs the device list to resolve v1
        // names to endpoint ids.
        var engine = new global::Volco.Windows.Audio.AudioEngine();
        bool engineReady = engine.Start();
        _audioEngine = engine;

        _runtime = new VolcoRuntime(_configManager, _audioEngine, engineReady);
        _runtime.Start();

        // Volume OSD flyout — VolumeOsdWindow.Show marshals to the UI thread itself, so this
        // subscription needs no dispatcher of its own even though VolumeChanged can be raised
        // from any thread.
        _osd = new VolumeOsdWindow();
        _runtime.VolumeChanged += (_, n) => _osd.Show(n);

        // Tray icon
        SetupTrayIcon();

        // A tray tooltip needs hovering to notice, and the log needs knowing to look — if audio
        // startup failed, put a real window in front of the user right away instead of leaving
        // them with silently dead sliders.
        if (_runtime.AudioEngineFailed)
        {
            ShowSettings();
        }

        AppLog.Info("App launch complete");
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new TaskbarIcon
        {
            IconSource = new GeneratedIconSource
            {
                Text = "V",
                Background = new SolidColorBrush(ColorHelper.FromArgb(255, 30, 100, 200)),
                Foreground = new SolidColorBrush(Colors.White),
            },
            ToolTipText = _runtime.AudioEngineFailed
                ? "Volco — audio engine failed to start; see volco.log"
                : "Volco",
        };

        var menu = new MenuFlyout();

        var openItem = new MenuFlyoutItem { Text = "Open Settings" };
        openItem.Click += (_, _) =>
        {
            AppLog.Info("Open Settings menu item clicked");
            ShowSettings();
        };
        menu.Items.Add(openItem);

        // The in-app updater only makes sense for official release builds — a local/dev build
        // shouldn't overwrite the developer's working copy.
        if (BuildInfo.IsRelease)
        {
            var updateItem = new MenuFlyoutItem { Text = "Check for Updates…" };
            updateItem.Click += (_, _) =>
            {
                AppLog.Info("Check for Updates menu item clicked");
                ShowUpdates();
            };
            menu.Items.Add(updateItem);
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        var exitItem = new MenuFlyoutItem { Text = "Exit" };
        exitItem.Click += (_, _) =>
        {
            AppLog.Info("Exit menu item clicked");
            Shutdown();
        };
        menu.Items.Add(exitItem);

        _trayIcon.ContextFlyout = menu;
        _trayIcon.ContextMenuMode = ContextMenuMode.SecondWindow;
        _trayIcon.LeftClickCommand = new RelayCommand(() =>
        {
            AppLog.Info("Tray icon left-clicked");
            ShowSettings();
        });

        _trayIcon.ForceCreate();
        AppLog.Info("Tray icon created");
    }

    /// <summary>
    /// Requested by the updater after it has staged a new build and launched the swap helper.
    /// Tears the app down cleanly so the helper can replace the exe and relaunch.
    /// </summary>
    public static void RequestExit() => _instance.Shutdown();

    private void Shutdown()
    {
        AppLog.Info("Shutting down");
        IsExiting = true;
        // Null-guarded (review items 4/9): OnLaunched can abort (e.g. an exception escaping into
        // UnhandledException) before these are assigned, and Shutdown() must still be able to run.
        // Runtime first: it owns the transport and the input pipeline, and must stop touching the
        // engine before the engine itself is disposed.
        _runtime?.Dispose();
        _audioEngine?.Dispose();
        _osd?.Close();
        _updateWindow?.Close();
        _trayIcon?.Dispose();
        Exit();
    }

    private void ShowUpdates()
    {
        AppLog.Info("ShowUpdates called");
        try
        {
            if (_updateWindow == null)
            {
                _updateWindow = new UpdateWindow(_configManager);
                _updateWindow.Closed += (_, _) => _updateWindow = null;
                AppLog.Info("UpdateWindow created");
            }
            _updateWindow.Activate();
        }
        catch (Exception ex)
        {
            AppLog.Error($"ShowUpdates failed: {ex}");
        }
    }

    private void ShowSettings()
    {
        AppLog.Info("ShowSettings called");
        try
        {
            if (_settingsWindow == null)
            {
                _settingsWindow = new SettingsWindow(_runtime);
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
                AppLog.Info("SettingsWindow created");
            }
            _settingsWindow.Activate();
            AppLog.Info("SettingsWindow activated");
        }
        catch (Exception ex)
        {
            AppLog.Error($"ShowSettings failed: {ex}");
        }
    }

    private class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action _execute;
        public RelayCommand(Action execute) => _execute = execute;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute();
#pragma warning disable 67
        public event EventHandler? CanExecuteChanged;
#pragma warning restore 67
    }
}
