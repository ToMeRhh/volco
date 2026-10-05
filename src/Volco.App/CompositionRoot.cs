using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Volco.App.Osd;
using Volco.App.ViewModels;
using Volco.App.Views;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Diagnostics;
using Volco.Core.Controller;
using Volco.Core.Firmware;
using Volco.Core.Input;
using Volco.Core.Runtime;
using Volco.Core.Updates;

namespace Volco.App;

/// <summary>
/// Owns startup and shutdown. Startup: ConfigManager + Load, createPlatform(), VolcoRuntime,
/// every runtime handler subscribed, and only then runtime.Start(); then tray, then settings if
/// the audio engine failed. Shutdown: runtime Dispose, engine Dispose, windows, tray.
/// </summary>
internal sealed class CompositionRoot : IDisposable
{
    private readonly string _configPath;
    private readonly Func<PlatformServices> _createPlatform;
    private readonly Func<ConnectionConfig, IControllerConnection?>? _connectionFactory;
    private readonly bool _createTray;

    private ConfigManager? _config;
    private PlatformServices? _platform;
    private VolcoRuntime? _runtime;
    private SettingsWindow? _settingsWindow;
    private SettingsViewModel? _settingsViewModel;
    private VolumeOsdWindow? _osd;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _firmwareMenuItem;
    private bool _trayShowsUpdate;
    private bool _trayShowsAttention;
    private FirmwareService? _firmware;
    private EventHandler<VolumeChangeNotification>? _osdHandler;
    private bool _shutDown;

    public CompositionRoot(string configPath, Func<PlatformServices> createPlatform,
                           Func<ConnectionConfig, IControllerConnection?>? connectionFactory = null,
                           bool createTray = true)
    {
        _configPath = configPath;
        _createPlatform = createPlatform;
        _connectionFactory = connectionFactory;
        _createTray = createTray;
    }

    public VolcoRuntime? Runtime => _runtime;
    public SettingsWindow? SettingsWindow => _settingsWindow;

    public void Start()
    {
        AppLog.Info($"App launching: Volco {BuildInfo.DisplayVersion} ({BuildInfo.BuildDetails?.Replace("\n", " · ") ?? "no build details"})");

        _config = new ConfigManager(_configPath);
        _config.Load();

        // Audio first: the runtime's v1 to v2 migration needs the device list.
        _platform = _createPlatform();

        var catalog = FirmwareCatalog.Load(_platform.Firmware ?? EmptyFirmwareSource.Instance);
        _runtime = new VolcoRuntime(_config, _platform.Audio, _platform.AudioReady, _connectionFactory,
                                    new VolcoRuntimeOptions { Firmware = catalog });
        _firmware = new FirmwareService(_runtime, _platform.Flasher, catalog);

        // Rule (a): the OSD is subscribed before Start(). Show is thread-safe and posts to the UI
        // thread itself (rule b), so the handler calls it directly.
        _osd = new VolumeOsdWindow(_platform.WindowEffects);
        var osd = _osd;
        _osdHandler = (_, n) => osd.Show(n);
        _runtime.VolumeChanged += _osdHandler;

        // Every handler is subscribed above; only now may the runtime start raising events.
        _runtime.Start();

        if (_createTray) CreateTray();
        _runtime.ControllerConfig.BannerChanged += OnBannerChanged;

        // A tooltip needs hovering to notice; if audio failed, put a window in front of the user.
        if (_runtime.AudioEngineFailed) ShowSettings();

        AppLog.Info("App launch complete");
    }

    /// <summary>Separate from Start so headless tests (no tray on that platform) can skip it.</summary>
    private void CreateTray()
    {
        try
        {
            var app = Application.Current;
            if (app is null) return;

            using var iconStream = AssetLoader.Open(new Uri("avares://Volco.App/Assets/volco.ico"));
            var tray = new TrayIcon
            {
                Icon = new WindowIcon(iconStream),
                ToolTipText = _runtime!.AudioEngineFailed
                    ? "Volco — audio engine failed to start; see volco.log"
                    : $"Volco {BuildInfo.DisplayVersion}",
                Command = new ActionCommand(() =>
                {
                    AppLog.Info("Tray icon left-clicked");
                    ShowSettings();
                }),
            };

            var menu = new NativeMenu();
            var open = new NativeMenuItem("Open Settings");
            open.Click += (_, _) =>
            {
                AppLog.Info("Open Settings menu item clicked");
                ShowSettings();
            };
            menu.Items.Add(open);

            // Shown while a controller firmware update is available (spec §8.3).
            _firmwareMenuItem = new NativeMenuItem("Update controller firmware…") { IsVisible = false };
            _firmwareMenuItem.Click += (_, _) =>
            {
                AppLog.Info("Update controller firmware menu item clicked");
                ShowSettings();
                _settingsWindow?.ShowPage("controller");
            };
            menu.Items.Add(_firmwareMenuItem);

            // The in-app updater only makes sense for official release builds.
            if (BuildInfo.IsRelease)
            {
                var update = new NativeMenuItem("Check for Updates…");
                update.Click += (_, _) => CheckForUpdates();
                menu.Items.Add(update);
            }

            menu.Items.Add(new NativeMenuItemSeparator());

            var exit = new NativeMenuItem("Exit");
            exit.Click += (_, _) =>
            {
                AppLog.Info("Exit menu item clicked");
                VolcoApplication.RequestExit();
            };
            menu.Items.Add(exit);

            tray.Menu = menu;
            TrayIcon.SetIcons(app, new TrayIcons { tray });
            _trayIcon = tray;
            UpdateTrayFirmware();
            AppLog.Info("Tray icon created");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Tray icon creation failed: {ex}");
        }
    }

    /// <summary>The tray's update state (spec §8.3): the icon with a dot, a tooltip line and the
    /// menu item, all from the runtime banner's <c>FirmwareUpdateAvailable</c>.</summary>
    internal bool TrayShowsFirmwareUpdate => _trayShowsUpdate;
    internal bool TrayShowsAttention => _trayShowsAttention;

    private void OnBannerChanged(object? sender, EventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateTrayFirmware);

    private void UpdateTrayFirmware()
    {
        if (_runtime is null || _shutDown) return;
        var banner = _runtime.ControllerConfig.Banner;
        var available = banner.FirmwareUpdateAvailable;
        var attention = banner.NeedsAttention;
        if (available == _trayShowsUpdate && attention == _trayShowsAttention && _trayIcon is not null) return;
        _trayShowsUpdate = available;
        _trayShowsAttention = attention;
        if (_trayIcon is not { } tray) return;
        try
        {
            using var icon = AssetLoader.Open(new Uri(attention
                ? "avares://Volco.App/Assets/volco-update.ico" : "avares://Volco.App/Assets/volco.ico"));
            tray.Icon = new WindowIcon(icon);
            var baseTip = _runtime.AudioEngineFailed
                ? "Volco — audio engine failed to start; see volco.log"
                : $"Volco {BuildInfo.DisplayVersion}";
            tray.ToolTipText = available ? $"{baseTip}\nController firmware update available"
                : banner.Kind == BannerKind.NoAnswer ? $"{baseTip}\nNo controller answering"
                : attention ? $"{baseTip}\nThe controller needs setting up" : baseTip;
            if (_firmwareMenuItem is not null)
            {
                _firmwareMenuItem.IsVisible = attention;
                _firmwareMenuItem.Header = available ? "Update controller firmware…" : "Set up the controller…";
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"Tray update state failed: {ex.Message}");
        }
    }

    public void ShowSettings()
    {
        AppLog.Info("ShowSettings called");
        try
        {
            if (_runtime is null) return;
            if (_settingsWindow is null)
            {
                _settingsViewModel = new SettingsViewModel(_config!, _runtime.Audio, _runtime, new AvaloniaUiDispatcher(), _firmware);
                _settingsWindow = new SettingsWindow(_settingsViewModel, _runtime);
                _settingsWindow.Closed += (_, _) =>
                {
                    _settingsWindow = null;
                    _settingsViewModel?.Dispose();
                    _settingsViewModel = null;
                };
                AppLog.Info("SettingsWindow created");
            }
            if (!_settingsWindow.IsVisible) _settingsWindow.Show();
            _settingsWindow.Activate();
        }
        catch (Exception ex)
        {
            AppLog.Error($"ShowSettings failed: {ex}");
        }
    }

    /// <summary>Menu target for "Check for Updates…": the Updates panel lives on the General page.</summary>
    public void CheckForUpdates()
    {
        AppLog.Info("Check for updates requested");
        ShowSettings();
        _settingsWindow?.ShowPage("general");
    }

    public void Shutdown()
    {
        if (_shutDown) return;
        _shutDown = true;
        AppLog.Info("Shutting down");

        // The settings ViewModel owns runtime subscriptions; drop them before the runtime goes.
        _settingsViewModel?.Dispose();
        _settingsViewModel = null;

        // Unsubscribe the OSD before closing it; a never-shown window may not raise Closed.
        if (_runtime is not null && _osdHandler is not null) _runtime.VolumeChanged -= _osdHandler;
        _osdHandler = null;

        if (_runtime is not null) _runtime.ControllerConfig.BannerChanged -= OnBannerChanged;

        // Runtime first: it owns the transport and input pipeline and must stop touching the
        // engine before the engine is disposed. It does not dispose the engine itself.
        _runtime?.Dispose();
        _platform?.Audio.Dispose();

        if (_osd is { } osdWindow)
        {
            _osd = null;
            osdWindow.Close();
        }

        if (_settingsWindow is { } w)
        {
            w.AllowClose = true;
            w.Close();
            _settingsWindow = null;
        }

        if (_trayIcon is { } tray)
        {
            tray.IsVisible = false;
            tray.Dispose();
            _trayIcon = null;
        }

        AppLog.Flush();
    }

    public void Dispose() => Shutdown();

    private sealed class ActionCommand(Action execute) : ICommand
    {
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
#pragma warning disable 67
        public event EventHandler? CanExecuteChanged;
#pragma warning restore 67
    }
}
