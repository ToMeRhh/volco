using System.Collections.ObjectModel;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Volco.Core.Communication;
using Volco.Core.Config;

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

    // Captured on construction (UI thread) so StatusChanged callbacks can marshal
    // back to the UI thread via TryEnqueue.
    private readonly DispatcherQueue? _dispatcher;

    // ── Connection type ────────────────────────────────────────────────────────

    [ObservableProperty]
    private ConnectionType _connectionType;

    // Derived visibility helpers (bool; views convert to Visibility)
    public bool IsSerial    => ConnectionType == ConnectionType.Serial;
    public bool IsUdp       => ConnectionType == ConnectionType.Udp;
    public bool IsWebSocket => ConnectionType == ConnectionType.WebSocket;

    // ── ComboBox index helpers (x:Bind two-way to ComboBox.SelectedIndex) ─────

    /// <summary>
    /// Zero-based index mapping: 0 = Serial, 1 = UDP, 2 = WebSocket.
    /// Used by the connection-type ComboBox in ConnectionTab.xaml.
    /// </summary>
    public int ConnectionTypeIndex
    {
        get => ConnectionType switch
        {
            ConnectionType.Serial    => 0,
            ConnectionType.Udp       => 1,
            ConnectionType.WebSocket => 2,
            _                        => 0,
        };
        set => ConnectionType = value switch
        {
            0 => ConnectionType.Serial,
            1 => ConnectionType.Udp,
            2 => ConnectionType.WebSocket,
            _ => ConnectionType.Serial,
        };
    }

    /// <summary>
    /// Zero-based index into the baud-rate list: 9600=0, 19200=1, 38400=2, 57600=3, 115200=4.
    /// Used by the baud-rate ComboBox in ConnectionTab.xaml.
    /// </summary>
    public int BaudRateIndex
    {
        get => BaudRate switch
        {
            9600   => 0,
            19200  => 1,
            38400  => 2,
            57600  => 3,
            115200 => 4,
            _      => 4, // default to 115200
        };
        set => BaudRate = value switch
        {
            0 => 9600,
            1 => 19200,
            2 => 38400,
            3 => 57600,
            4 => 115200,
            _ => 115200,
        };
    }

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

        // Capture the UI-thread dispatcher so background StatusChanged events
        // can be marshalled safely (DispatcherQueue is not a XAML visual type).
        _dispatcher = DispatcherQueue.GetForCurrentThread();

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
        var previous = ComPort;

        ComPorts.Clear();
        foreach (var port in SerialPort.GetPortNames())
            ComPorts.Add(port);

        // ComPorts.Clear() drives the two-way-bound ComboBox.SelectedItem — and therefore
        // ComPort — to null via OnComPortChanged before the list is repopulated. The guard in
        // OnComPortChanged stops that from persisting a blank port or restarting the connection,
        // but it still leaves the backing field blank. Restore it directly here (bypassing the
        // generated setter/partial hook, so this doesn't itself trigger a redundant save or
        // connection restart) when the previously selected port is still present.
        if (!string.IsNullOrEmpty(previous) && ComPorts.Contains(previous))
        {
            _comPort = previous;
            OnPropertyChanged(nameof(ComPort));
        }
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
    {
        // StatusChanged may fire on a background thread; marshal to UI thread.
        if (_dispatcher != null)
            _dispatcher.TryEnqueue(() => UpdateStatus(status));
        else
            UpdateStatus(status);
    }

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
    /// changes after a config save restarts the hardware transport. App.StartConnection
    /// can run on a background thread (e.g. the ConfigManager debounce-timer thread when a
    /// connection-affecting property changes) rather than the UI thread, but this mutates
    /// bound state (Status/StatusText via UpdateStatus, plus the event subscription), so it
    /// must marshal the same way OnConnectionStatusChanged already does.
    /// </summary>
    public void UpdateConnection(IControllerConnection? newConnection)
    {
        if (_dispatcher != null)
            _dispatcher.TryEnqueue(() => UpdateConnectionOnUiThread(newConnection));
        else
            UpdateConnectionOnUiThread(newConnection);
    }

    private void UpdateConnectionOnUiThread(IControllerConnection? newConnection)
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
        OnPropertyChanged(nameof(ConnectionTypeIndex));
        Persist();
    }

    partial void OnComPortChanged(string value)
    {
        // A null/empty port only ever arises from the bound ObservableCollection being cleared
        // during RefreshComPorts (Bug 2 - clearing ComPorts drives the two-way-bound ComboBox
        // selection, and therefore ComPort, to null) — never real user intent, since the picker
        // has no blank/"None" entry. Ignore it: no persist, no connection restart.
        if (string.IsNullOrEmpty(value)) return;
        _config.Config.Connection.Serial.ComPort = value;
        Persist();
    }

    partial void OnBaudRateChanged(int value)
    {
        _config.Config.Connection.Serial.BaudRate = value;
        OnPropertyChanged(nameof(BaudRateIndex));
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
