using System.Collections.ObjectModel;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Communication;
using Volco.Config;

namespace Volco.UI.ViewModels;

/// <summary>
/// ViewModel for the Connection settings tab.
/// Manages connection type, sub-protocol settings, COM-port enumeration, and
/// live connection-status display. All writes call
/// <c>RequestSave(connectionChanged: true)</c> so App restarts the hardware connection.
/// </summary>
public partial class ConnectionViewModel : ObservableObject
{
    private readonly ConfigManager _config;
    private IControllerConnection? _connection;

    // ── Connection type ────────────────────────────────────────────────────────

    [ObservableProperty]
    private ConnectionType _connectionType;

    // Derived visibility helpers (bool; views convert to Visibility)
    public bool IsSerial    => ConnectionType == ConnectionType.Serial;
    public bool IsUdp       => ConnectionType == ConnectionType.Udp;
    public bool IsWebSocket => ConnectionType == ConnectionType.WebSocket;

    // ── Serial settings ────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _comPort = "";

    [ObservableProperty]
    private int _baudRate;

    public ObservableCollection<string> ComPorts { get; } = new();

    // ── UDP settings ───────────────────────────────────────────────────────────

    [ObservableProperty]
    private int _udpListenPort;

    // ── WebSocket settings ─────────────────────────────────────────────────────

    [ObservableProperty]
    private int _webSocketPort;

    // ── Live status (no XAML types — views map Status/StatusText to colour) ───

    [ObservableProperty]
    private ConnectionStatus _status = ConnectionStatus.Disconnected;

    [ObservableProperty]
    private string _statusText = "Disconnected";

    // ── Constructor ────────────────────────────────────────────────────────────

    public ConnectionViewModel(ConfigManager config, IControllerConnection? connection)
    {
        _config = config;
        _connection = connection;

        var cfg = config.Config.Connection;
        _connectionType  = cfg.Type;
        _comPort         = cfg.Serial.ComPort;
        _baudRate        = cfg.Serial.BaudRate;
        _udpListenPort   = cfg.Udp.ListenPort;
        _webSocketPort   = cfg.WebSocket.Port;

        RefreshComPortsCommand.Execute(null);
        SubscribeToConnection();
        UpdateStatus(_connection?.Status ?? ConnectionStatus.Disconnected);
    }

    // ── COM-port refresh command ───────────────────────────────────────────────

    [RelayCommand]
    private void RefreshComPorts()
    {
        ComPorts.Clear();
        foreach (var port in SerialPort.GetPortNames())
            ComPorts.Add(port);
    }

    // ── Status subscription helpers ────────────────────────────────────────────

    private void SubscribeToConnection()
    {
        if (_connection != null)
            _connection.StatusChanged += OnConnectionStatusChanged;
    }

    private void UnsubscribeFromConnection()
    {
        if (_connection != null)
            _connection.StatusChanged -= OnConnectionStatusChanged;
    }

    private void OnConnectionStatusChanged(object? sender, ConnectionStatus status)
        => UpdateStatus(status);

    private void UpdateStatus(ConnectionStatus status)
    {
        Status = status;
        StatusText = status switch
        {
            ConnectionStatus.Connected    => "Connected",
            ConnectionStatus.Disconnected => "Disconnected",
            ConnectionStatus.Error        => "Error",
            _                             => status.ToString()
        };
    }

    /// <summary>
    /// Called by the owner (SettingsViewModel / App) when the connection object
    /// changes after a config save restarts the hardware transport.
    /// </summary>
    public void UpdateConnection(IControllerConnection? newConnection)
    {
        UnsubscribeFromConnection();
        _connection = newConnection;
        SubscribeToConnection();
        UpdateStatus(_connection?.Status ?? ConnectionStatus.Disconnected);
    }

    // ── Property-change hooks — write back to config ───────────────────────────

    partial void OnConnectionTypeChanged(ConnectionType value)
    {
        _config.Config.Connection.Type = value;
        OnPropertyChanged(nameof(IsSerial));
        OnPropertyChanged(nameof(IsUdp));
        OnPropertyChanged(nameof(IsWebSocket));
        Persist();
    }

    partial void OnComPortChanged(string value)
    {
        _config.Config.Connection.Serial.ComPort = value;
        Persist();
    }

    partial void OnBaudRateChanged(int value)
    {
        _config.Config.Connection.Serial.BaudRate = value;
        Persist();
    }

    partial void OnUdpListenPortChanged(int value)
    {
        _config.Config.Connection.Udp.ListenPort = value;
        Persist();
    }

    partial void OnWebSocketPortChanged(int value)
    {
        _config.Config.Connection.WebSocket.Port = value;
        Persist();
    }

    private void Persist() => _config.RequestSave(connectionChanged: true);
}
