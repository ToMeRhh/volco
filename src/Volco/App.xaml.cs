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
    private IAudioManager _audioManager = null!;
    private IAudioEngine _audioEngine = null!;
    private IControllerConnection? _connection;
    private ButtonPressDetector _buttonPressDetector = null!;
    private SliderProcessor _sliderProcessor = null!;
    private VolumeOsdWindow _osd = null!;

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
        _audioManager = new AudioManager();
        _audioEngine = new LegacyAudioEngineAdapter(_audioManager);

        if (ConfigMigrator.NeedsMigration(_configManager.Config))
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
            ToolTipText = "Volco",
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
        _sliderProcessor.Dispose();
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
                _settingsWindow = new SettingsWindow(_configManager, _audioManager, _connection, _sliderProcessor);
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
        action?.Execute();

        ShowMuteOsd(actionConfig);

        // Push updated state to controller after action changes audio state
        PushControllerState();
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

        var config = _configManager.Config;

        // Push mute state for mute buttons (indices 0 and 1)
        for (int i = 0; i < Math.Min(2, config.Buttons.Count); i++)
        {
            var isMuted = GetMuteStateForButton(config.Buttons[i]);
            if (isMuted.HasValue)
                _connection.Send(ProtocolEncoder.EncodeMuteState(i, isMuted.Value));
        }

        // Push active device
        var currentDevice = _audioManager.GetDefaultOutputDeviceName();
        if (currentDevice != null)
        {
            var devices = config.OutputDevices;
            int activeIndex = currentDevice.Contains(devices.Device1.FriendlyName, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            _connection.Send(ProtocolEncoder.EncodeActiveDevice(activeIndex));
        }
    }

    private bool? GetMuteStateForButton(ButtonConfig buttonConfig)
    {
        // Scan all press types for the first mute-related action
        foreach (var actionConfig in new[] { buttonConfig.SinglePress, buttonConfig.DoublePress, buttonConfig.LongPress })
        {
            switch (actionConfig.Action)
            {
                case ButtonActionType.MuteCurrentOutput:
                    return _audioEngine.TryGetDeviceState(
                        DeviceRef.Default(AudioFlow.Render, AudioRole.Multimedia), out _, out var outputMuted) && outputMuted;
                case ButtonActionType.MuteSpecificDevice:
                    var specificDevice = ActionFactory.Resolve(actionConfig.Device);
                    if (specificDevice == null) return null; // unresolvable device config; nothing to report
                    return _audioEngine.TryGetDeviceState(specificDevice.Value, out _, out var specificMuted) && specificMuted;
                case ButtonActionType.MuteCurrentInput:
                    return _audioEngine.TryGetDeviceState(
                        DeviceRef.Default(AudioFlow.Capture, AudioRole.Communications), out _, out var inputMuted) && inputMuted;
            }
        }
        return null;
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
