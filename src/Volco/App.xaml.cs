using H.NotifyIcon;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Volco.Audio;
using Volco.Communication;
using Volco.Config;
using Volco.Input;
using Volco.Input.Actions;
using Volco.UI;
using Volco.Updates;

namespace Volco;

public partial class App : Application
{
    public static bool IsExiting { get; private set; }
    public IControllerConnection? Connection => _connection;
    private static App _instance = null!;
    private TaskbarIcon? _trayIcon;
    private SettingsWindow? _settingsWindow;
    private UpdateWindow? _updateWindow;
    private ConfigManager _configManager = null!;
    private IAudioEngine _audioEngine = null!;
    private IControllerConnection? _connection;
    private ButtonPressDetector _buttonPressDetector = null!;
    private SliderProcessor _sliderProcessor = null!;
    private VolumeOsdWindow _osd = null!;
    private bool _audioEngineStartFailed;

    /// <summary>
    /// Set right before <see cref="ActionFactory.Create"/>'s action for a mute-type button press
    /// is executed, cleared by <see cref="OnDeviceVolumeChanged"/> once ITS OWN write for the
    /// expected device lands. Execute() only posts a command to the audio thread (review item 3)
    /// - reading state synchronously right after it returns, as this used to, sees pre-write
    /// state. Matched by device id (not just "any change") so a press against a device that
    /// never actually resolves (e.g. an unplugged headset - see review item 2) does not leave a
    /// dangling flag that pops an unrelated OSD the next time some other device's volume changes.
    /// </summary>
    private sealed record PendingMuteOsd(string DeviceId, ButtonActionConfig Config);
    private PendingMuteOsd? _pendingMuteOsd;

    public App()
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

        // Audio first — the migrator needs the device list to resolve v1 names to endpoint ids.
        var engine = new Volco.Audio.Windows.AudioEngine();
        bool engineReady = engine.Start();
        _audioEngine = engine;
        _audioEngineStartFailed = !engineReady;
        _audioEngine.DeviceVolumeChanged += OnDeviceVolumeChanged;
        _audioEngine.DevicesChanged += OnDevicesChangedForControllerState;

        if (_audioEngineStartFailed)
        {
            AppLog.Error("AudioEngine.Start() did not signal ready within its timeout; the audio " +
                         "thread likely failed to come up. Volco will run but will not be able to " +
                         "control audio until this is resolved — see volco.log.");
        }

        // Skipped entirely when the engine isn't ready (review item 4): DeviceRegistry.Start()
        // may still be running on the audio thread past the timeout, and GetDevices() here would
        // race its unsynchronised-at-the-time dictionary writes. Calling MigrateFile with an
        // empty device list is also a data-loss trap in its own right (review item 5) - it
        // reduces to a no-op below anyway, but skipping the call here avoids depending on that.
        if (!_audioEngineStartFailed && ConfigMigrator.NeedsMigration(_configManager.Config))
        {
            AppLog.Info("Migrating config to schema v2");
            ConfigMigrator.MigrateFile(configPath, _configManager.Config,
                                       _audioEngine.GetDevices(AudioFlow.Render));
            _configManager.Save();
        }

        _configManager.ConnectionConfigChanged += OnConfigChanged;

        // Input processing
        _buttonPressDetector = new ButtonPressDetector();
        _buttonPressDetector.PressDetected += OnButtonPressDetected;
        _sliderProcessor = new SliderProcessor(_audioEngine, _configManager);

        // Volume OSD flyout
        _osd = new VolumeOsdWindow();
        _sliderProcessor.VolumeChanged += (_, n) => _osd.Show(n);

        // Connection
        StartConnection();

        // Tray icon
        SetupTrayIcon();

        // A tray tooltip needs hovering to notice, and the log needs knowing to look — if audio
        // startup failed, put a real window in front of the user right away instead of leaving
        // them with silently dead sliders.
        if (_audioEngineStartFailed)
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
            ToolTipText = _audioEngineStartFailed
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
        _connection?.Dispose();
        // Null-guarded (review items 4/9): OnLaunched can abort (e.g. an exception escaping into
        // UnhandledException) before these are assigned, and Shutdown() must still be able to run.
        _sliderProcessor?.Dispose();
        if (_audioEngine is not null)
        {
            _audioEngine.DeviceVolumeChanged -= OnDeviceVolumeChanged;
            _audioEngine.DevicesChanged -= OnDevicesChangedForControllerState;
            _audioEngine.Dispose();
        }
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
                _settingsWindow = new SettingsWindow(_configManager, _audioEngine, _connection, _sliderProcessor);
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

    private void StartConnection()
    {
        _connection?.Dispose();
        var config = _configManager.Config.Connection;
        AppLog.Info($"Starting connection: {config.Type}");
        _connection = config.Type switch
        {
            ConnectionType.Serial => new SerialConnection(config.Serial),
            ConnectionType.Udp => new UdpConnection(config.Udp),
            ConnectionType.WebSocket => new WebSocketConnection(config.WebSocket),
            _ => null
        };

        if (_connection == null) return;

        _connection.MessageReceived += OnMessageReceived;
        _connection.StatusChanged += OnConnectionStatusChanged;
        _connection.Connect();

        _settingsWindow?.UpdateConnection(_connection);
    }

    private void OnConnectionStatusChanged(object? sender, ConnectionStatus status)
    {
        AppLog.Info($"Connection status changed: {status}");
        if (status == ConnectionStatus.Connected)
            PushControllerState();
    }

    private void OnMessageReceived(object? sender, ControllerMessage msg)
    {
        switch (msg)
        {
            case SliderMessage slider:
                AppLog.Debug($"Slider event: Index={slider.SliderIndex}, Value={slider.Value}");
                _sliderProcessor.HandleSliderEvent(slider.SliderIndex, slider.Value);
                break;
            case ButtonMessage button:
                AppLog.Debug($"Button event: Index={button.ButtonIndex}, State={button.State}");
                var bcfg = _configManager.Config.Buttons.ElementAtOrDefault(button.ButtonIndex);
                _buttonPressDetector.HandleButtonEvent(button.ButtonIndex, button.State,
                    bcfg?.LongPressMs ?? 500, bcfg?.DoublePressWindowMs ?? 300);
                break;
        }
    }

    private void OnButtonPressDetected(object? sender, ButtonPressEventArgs e)
    {
        AppLog.Info($"Button press detected: Index={e.ButtonIndex}, Type={e.PressType}");
        var config = _configManager.Config;
        if (e.ButtonIndex < 0 || e.ButtonIndex >= config.Buttons.Count) return;

        var buttonConfig = config.Buttons[e.ButtonIndex];
        var actionConfig = e.PressType switch
        {
            PressType.Single => buttonConfig.SinglePress,
            PressType.Double => buttonConfig.DoublePress,
            PressType.Long => buttonConfig.LongPress,
            _ => null
        };

        if (actionConfig == null) return;
        var action = ActionFactory.Create(actionConfig, _audioEngine, _configManager);

        // action.Execute() only posts a command to the audio thread - the write hasn't happened
        // yet when Execute() returns (review item 3). Reading TryGetDeviceState/GetDefaultDevice
        // synchronously right here, as this used to, shows pre-toggle state: every mute press
        // flashed the wrong OSD glyph and pushed the wrong controller LED. Record which device
        // this mute action targets (if any) and let OnDeviceVolumeChanged show the OSD once ITS
        // OWN write for that device actually lands, with authoritative state.
        // PushControllerState() itself is now driven entirely off
        // OnDeviceVolumeChanged/OnDevicesChangedForControllerState below, which also closes spec
        // §3 item 3 for a change that didn't originate from a button at all (e.g. the Windows
        // volume mixer) - DeviceVolumeChanged previously had no subscribers at all.
        var expectedDeviceId = IsMuteAction(actionConfig.Action) ? ResolveExpectedMuteDeviceId(actionConfig) : null;
        _pendingMuteOsd = expectedDeviceId is null ? null : new PendingMuteOsd(expectedDeviceId, actionConfig);

        action?.Execute();
    }

    private static bool IsMuteAction(ButtonActionType type) => type is
        ButtonActionType.MuteCurrentOutput or
        ButtonActionType.MuteCurrentInput or
        ButtonActionType.MuteSpecificDevice;

    /// <summary>Best-effort endpoint id this mute action targets, resolved with the same
    /// read-only queries <see cref="ShowMuteOsd"/> already uses - null when it cannot be
    /// determined right now (e.g. an unconfigured or currently-disconnected device), in which
    /// case no OSD will be shown for this press at all, rather than a wrong one later.</summary>
    private string? ResolveExpectedMuteDeviceId(ButtonActionConfig actionConfig) => actionConfig.Action switch
    {
        ButtonActionType.MuteCurrentOutput =>
            _audioEngine.GetDefaultDevice(AudioFlow.Render, AudioRole.Multimedia)?.Id,
        ButtonActionType.MuteCurrentInput =>
            _audioEngine.GetDefaultDevice(AudioFlow.Capture, AudioRole.Communications)?.Id,
        ButtonActionType.MuteSpecificDevice => ResolveSpecificDeviceId(actionConfig.Device),
        _ => null,
    };

    private string? ResolveSpecificDeviceId(DeviceRefConfig? cfg)
    {
        if (cfg == null) return null;
        if (!string.IsNullOrEmpty(cfg.Id)) return cfg.Id;
        if (string.IsNullOrEmpty(cfg.FriendlyName)) return null;
        return _audioEngine.GetDevices(AudioFlow.Render)
            .FirstOrDefault(d => string.Equals(d.FriendlyName, cfg.FriendlyName, StringComparison.OrdinalIgnoreCase))
            ?.Id;
    }

    /// <summary>
    /// Fired by <see cref="IAudioEngine.DeviceVolumeChanged"/> - on the audio thread, with
    /// authoritative post-write level/mute state, whether this app caused the change or it came
    /// in externally (e.g. the Windows volume mixer). Always refreshes controller LEDs (cheap,
    /// and the only way an externally-originated change ever reaches the controller). Shows the
    /// mute OSD only for this app's own write (<see cref="DeviceVolumeChangedEventArgs.IsOwnChange"/>)
    /// to the exact device a pending mute press targeted (see <see cref="_pendingMuteOsd"/>) - do
    /// not call back into the engine with a write here, only the read-only queries
    /// ShowMuteOsd/PushControllerState already make, which are documented safe from any thread;
    /// VolumeOsdWindow.Show marshals to the UI thread itself.
    /// </summary>
    private void OnDeviceVolumeChanged(object? sender, DeviceVolumeChangedEventArgs e)
    {
        PushControllerState();

        var pending = _pendingMuteOsd;
        if (pending is not null && e.IsOwnChange && string.Equals(pending.DeviceId, e.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            _pendingMuteOsd = null;
            ShowMuteOsd(pending.Config);
        }
    }

    /// <summary>
    /// Fired by <see cref="IAudioEngine.DevicesChanged"/> - on the audio thread. Only
    /// <see cref="DeviceChangeKind.DefaultChanged"/> matters here: it is the authoritative,
    /// post-write counterpart of a ToggleOutputDevice/SetOutputDevice button press (the same
    /// staleness problem DeviceVolumeChanged solves for mute/volume - see
    /// <see cref="OnDeviceVolumeChanged"/> - applies to the active-device LED too, since a
    /// default-device switch is posted to the audio thread the same way).
    /// </summary>
    private void OnDevicesChangedForControllerState(object? sender, DevicesChangedEventArgs e)
    {
        if (e.Kind == DeviceChangeKind.DefaultChanged)
        {
            PushControllerState();
        }
    }

    private void ShowMuteOsd(ButtonActionConfig actionConfig)
    {
        string? label;
        bool muted;
        switch (actionConfig.Action)
        {
            case ButtonActionType.MuteCurrentOutput:
                label = _audioEngine.GetDefaultDevice(AudioFlow.Render, AudioRole.Multimedia)?.FriendlyName ?? "Output";
                muted = _audioEngine.TryGetDeviceState(
                    DeviceRef.Default(AudioFlow.Render, AudioRole.Multimedia), out _, out var outputMuted) && outputMuted;
                break;
            case ButtonActionType.MuteSpecificDevice:
                var specificDevice = ActionFactory.Resolve(actionConfig.Device);
                if (specificDevice == null) return; // unresolvable device config; nothing to show
                label = actionConfig.Device?.FriendlyName ?? "Output";
                muted = _audioEngine.TryGetDeviceState(specificDevice.Value, out _, out var specificMuted) && specificMuted;
                break;
            case ButtonActionType.MuteCurrentInput:
                label = "Microphone";
                muted = _audioEngine.TryGetDeviceState(
                    DeviceRef.Default(AudioFlow.Capture, AudioRole.Communications), out _, out var inputMuted) && inputMuted;
                break;
            default:
                return; // non-mute actions don't show the OSD
        }

        _osd.Show(new VolumeChangeNotification(label, muted ? 0f : 1f, muted, OsdKind.Mute));
    }

    private void PushControllerState()
    {
        if (_connection?.Status != ConnectionStatus.Connected) return;

        foreach (var frame in ControllerStateReporter.BuildFrames(_configManager.Config, _audioEngine))
            _connection.Send(frame);
    }

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        AppLog.Info("Connection configuration changed, restarting connection");
        // Handler is invoked only when connection config changes
        StartConnection();
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
