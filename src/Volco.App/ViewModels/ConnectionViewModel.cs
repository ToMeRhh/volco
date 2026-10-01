using System.Collections.ObjectModel;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Runtime;

namespace Volco.App.ViewModels;

/// <summary>
/// ViewModel for the Connection settings tab.
/// Manages connection type, sub-protocol settings, COM-port enumeration, and
/// live connection-status display. All writes call
/// <c>RequestSave(connectionChanged: true)</c> so the runtime restarts the hardware connection.
/// </summary>
public sealed partial class ConnectionViewModel : ObservableObject, IDisposable
{
    private readonly ConfigManager _config;
    private readonly VolcoRuntime _runtime;

    // ConnectionStatusChanged can fire on any thread; the handler marshals through this.
    private readonly IUiDispatcher _ui;
    private bool _disposed;

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

    public ConnectionViewModel(ConfigManager config, VolcoRuntime runtime, IUiDispatcher ui)
    {
        _config = config;
        _runtime = runtime;

        _ui = ui;

        var cfg = config.Config.Connection;
        _connectionType  = cfg.Type;
        _comPort         = cfg.Serial.ComPort;
        _baudRate        = cfg.Serial.BaudRate;
        _udpListenPort   = cfg.Udp.ListenPort;
        _webSocketPort   = cfg.WebSocket.Port;

        RefreshComPortsCommand.Execute(null);
        // Survives transport restarts (the runtime re-attaches its own handler to each transport
        // it creates), so unlike the old per-transport subscription this never needs re-wiring.
        _runtime.ConnectionStatusChanged += OnConnectionStatusChanged;
        UpdateStatus(_runtime.ConnectionStatus);
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

    private void OnConnectionStatusChanged(object? sender, ConnectionStatus status)
    {
        // ConnectionStatusChanged may fire on any thread (audio thread, transport threads, timer
        // threads, the config debounce thread); marshal to the UI thread without blocking.
        _ui.Post(() =>
        {
            if (!_disposed) UpdateStatus(status);
        });
    }

    /// <summary>Unsubscribes from the runtime (carried rule d).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runtime.ConnectionStatusChanged -= OnConnectionStatusChanged;
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
