using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Volco.Audio;
using Volco.Communication;
using Volco.Config;
using Volco.Input;
using Volco.Input.Actions;
using Volco.UI;

namespace Volco;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private SettingsWindow? _settingsWindow;
    private ConfigManager _configManager = null!;
    private IAudioManager _audioManager = null!;
    private IControllerConnection? _connection;
    private ButtonPressDetector _buttonPressDetector = null!;
    private SliderProcessor _sliderProcessor = null!;
    private SessionMonitor _sessionMonitor = null!;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Config
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Volco", "config.json");
        _configManager = new ConfigManager(configPath);
        _configManager.Load();
        _configManager.ConfigChanged += OnConfigChanged;

        // Audio
        _audioManager = new AudioManager();
        _sessionMonitor = new SessionMonitor(_audioManager);

        // Input processing
        _buttonPressDetector = new ButtonPressDetector();
        _buttonPressDetector.PressDetected += OnButtonPressDetected;
        _sliderProcessor = new SliderProcessor(_audioManager, _configManager, _sessionMonitor);

        // Connection
        StartConnection();

        // Tray icon
        SetupTrayIcon();
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new TaskbarIcon();
        _trayIcon.ToolTipText = "Volco";

        var menu = new MenuFlyout();

        var openItem = new MenuFlyoutItem { Text = "Open Settings" };
        openItem.Click += (_, _) => ShowSettings();
        menu.Items.Add(openItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var exitItem = new MenuFlyoutItem { Text = "Exit" };
        exitItem.Click += (_, _) =>
        {
            _connection?.Dispose();
            _sessionMonitor.Dispose();
            _trayIcon?.Dispose();
            Exit();
        };
        menu.Items.Add(exitItem);

        _trayIcon.ContextFlyout = menu;
        _trayIcon.LeftClickCommand = new RelayCommand(ShowSettings);
    }

    private void ShowSettings()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_configManager, _audioManager);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Activate();
    }

    private void StartConnection()
    {
        _connection?.Dispose();
        var config = _configManager.Config.Connection;
        _connection = config.Type switch
        {
            ConnectionType.Serial => new SerialConnection(config.Serial),
            ConnectionType.Udp => new UdpConnection(config.Udp),
            _ => null
        };

        if (_connection == null) return;

        _connection.MessageReceived += OnMessageReceived;
        _connection.Connect();
    }

    private void OnMessageReceived(object? sender, ControllerMessage msg)
    {
        switch (msg)
        {
            case SliderMessage slider:
                _sliderProcessor.HandleSliderEvent(slider.SliderIndex, slider.Value);
                break;
            case ButtonMessage button:
                _buttonPressDetector.HandleButtonEvent(button.ButtonIndex, button.State);
                break;
        }
    }

    private void OnButtonPressDetected(object? sender, ButtonPressEventArgs e)
    {
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
        var action = ActionFactory.Create(actionConfig, _audioManager, _configManager);
        action?.Execute();
    }

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        // Restart connection if connection config might have changed
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
