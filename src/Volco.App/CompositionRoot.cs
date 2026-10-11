using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
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
using Volco.Core.Scripts;
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
    private NativeMenuItem? _trayConnectionItem;
    private NativeMenuItem? _firmwareMenuItem;
    private WindowIcon? _connectedIcon;
    private WindowIcon? _disconnectedIcon;
    private WindowIcon? _attentionIcon;
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
        // Scripts run through .NET's Process anywhere; media and programs need the platform's.
        var custom = new CustomActionServices(_platform.Media, _platform.Launcher, new SystemProcessStarter());
        _runtime = new VolcoRuntime(_config, _platform.Audio, _platform.AudioReady, _connectionFactory,
                                    new VolcoRuntimeOptions { Firmware = catalog }, custom);
        // The port a flash or probe settles on is saved on the UI thread, which owns the config.
        _firmware = new FirmwareService(_runtime, _platform.Flasher, catalog, configThread: a =>
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(() =>
            {
                try { a(); done.SetResult(); }
                catch (Exception e) { done.SetException(e); }
            });
            return done.Task;
        });
        var firmware = _firmware;
        VolcoApplication.Gate = new ExitGate(() => firmware.IsBusy);
        firmware.BusyChanged += (_, _) => Dispatcher.UIThread.Post(() => VolcoApplication.Gate?.OnBusyChanged());

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

            _connectedIcon = LoadIcon("volco.ico");
            _disconnectedIcon = LoadIcon("volco-disconnected.ico");
            _attentionIcon = LoadIcon("volco-update.ico");
            var tray = new TrayIcon
            {
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

            var connection = new NativeMenuItem("Disconnect");
            connection.Click += (_, _) =>
            {
                AppLog.Info($"{connection.Header} menu item clicked");
                if (_runtime!.IsUserDisconnected) _runtime.Connect();
                else _runtime.Disconnect();
            };
            menu.Items.Add(connection);
            _trayConnectionItem = connection;

            // Shown while the controller needs the user: a firmware update, no controller
            // answering, or a controller to set up (spec §8.3).
            _firmwareMenuItem = new NativeMenuItem("Update controller firmware…") { IsVisible = false };
            _firmwareMenuItem.Click += (_, _) =>
            {
                AppLog.Info($"{_firmwareMenuItem.Header} menu item clicked");
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

            _runtime!.LinkStatusChanged += OnTrayConnectionChanged;
            UpdateTray();
            AppLog.Info("Tray icon created");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Tray icon creation failed: {ex}");
        }
    }

    private static WindowIcon LoadIcon(string name)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://Volco.App/Assets/{name}"));
        return new WindowIcon(stream);
    }

    /// <summary>The tray's controller state (spec §8.3): the attention icon, a tooltip line and the
    /// menu item, from the runtime banner. Tracked even without a tray, for tests.</summary>
    internal bool TrayShowsFirmwareUpdate => _trayShowsUpdate;
    internal bool TrayShowsAttention => _trayShowsAttention;

    /// <summary>Runtime events come on any thread; the tray reads the runtime's current state.</summary>
    private void OnTrayConnectionChanged<T>(object? sender, T e) => Dispatcher.UIThread.Post(UpdateTray);

    private void OnBannerChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(UpdateTray);

    /// <summary>One place decides the tray: red while the controller isn't connected (manual
    /// Disconnect included, an expected wait not), else the attention icon while the controller
    /// needs the user, else the normal icon. The attention line and menu item show either way.</summary>
    private void UpdateTray()
    {
        if (_runtime is not { } runtime || _shutDown) return;
        var banner = runtime.ControllerConfig.Banner;
        _trayShowsUpdate = banner.FirmwareUpdateAvailable;
        _trayShowsAttention = banner.NeedsAttention;
        if (_trayIcon is not { } tray) return;

        var status = TrayStatus.For(runtime.AudioEngineFailed, runtime.LinkStatus, runtime.Config.Config.Connection);
        var attention = _trayShowsAttention;
        tray.Icon = status.Down ? _disconnectedIcon : attention ? _attentionIcon : _connectedIcon;
        // No second line where the headline already says it ("Controller not responding",
        // "Firmware update needed").
        tray.ToolTipText = !attention || banner.Kind is BannerKind.NoAnswer or BannerKind.Incompatible ? status.ToolTip
            : _trayShowsUpdate ? $"{status.ToolTip}\nController firmware update available"
            : banner.Kind is BannerKind.NotSetUp or BannerKind.NotSetUpHasMirror ? $"{status.ToolTip}\nThe controller needs setting up"
            : $"{status.ToolTip}\nThe controller needs attention";
        if (_trayConnectionItem is not null) _trayConnectionItem.Header = status.ConnectionMenuText;
        if (_firmwareMenuItem is not null)
        {
            _firmwareMenuItem.IsVisible = attention;
            _firmwareMenuItem.Header = _trayShowsUpdate ? "Update controller firmware…"
                : banner.Kind is BannerKind.NotSetUp or BannerKind.NotSetUpHasMirror ? "Set up the controller…"
                : "Open the Hardware page…";
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
        // After the runtime, so a mirror written while it disposed is saved too.
        _config?.FlushPendingSave();
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

        if (_runtime is not null)
        {
            _runtime.LinkStatusChanged -= OnTrayConnectionChanged;
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
