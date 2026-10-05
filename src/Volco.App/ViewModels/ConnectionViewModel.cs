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

    // One per segment of the "Connect using" control; setting one true selects it.
    public bool IsSerial
    {
        get => ConnectionType == ConnectionType.Serial;
        set { if (value) ConnectionType = ConnectionType.Serial; }
    }

    public bool IsUdp
    {
        get => ConnectionType == ConnectionType.Udp;
        set { if (value) ConnectionType = ConnectionType.Udp; }
    }

    public bool IsWebSocket
    {
        get => ConnectionType == ConnectionType.WebSocket;
        set { if (value) ConnectionType = ConnectionType.WebSocket; }
    }

    /// <summary>What the status panel says under the state, e.g. "COM3 · USB cable".</summary>
    public string TransportDetails => ConnectionType switch
    {
        ConnectionType.Serial => string.IsNullOrEmpty(ComPort)
            ? "USB cable · no port selected"
            : $"{ComPort} · USB cable",
        ConnectionType.Udp => $"Wi-Fi (UDP) · port {UdpListenPort}",
        ConnectionType.WebSocket => $"WebSocket · port {WebSocketPort}",
        _ => "",
    };

    public bool IsConnected => Status == ConnectionStatus.Connected;

    /// <summary>The navigation footer's text: "Connected · COM3", "Not connected", …</summary>
    public string FooterText => _runtime.ControllerState switch
    {
        ControllerState.VersionMismatch => "Firmware mismatch",
        ControllerState.AwaitingHello when _runtime.ControllerSilent => "No controller answering",
        ControllerState.AwaitingHello => "Waiting for the controller…",
        _ => Status switch
        {
            ConnectionStatus.Connected => ConnectionType switch
            {
                ConnectionType.Serial => $"Connected · {ComPort}",
                ConnectionType.Udp => "Connected · Wi-Fi",
                _ => "Connected · WebSocket",
            },
            ConnectionStatus.Error => "Connection error",
            _ => "Not connected",
        },
    };

    /// <summary>The status panel's headline: the port can be open with nothing answering on it.</summary>
    private string StatusFor(ConnectionStatus status) => status switch
    {
        ConnectionStatus.Connected => _runtime.ControllerState switch
        {
            ControllerState.AwaitingHello when _runtime.ControllerSilent => "No controller answering",
            ControllerState.AwaitingHello => "Waiting for the controller…",
            ControllerState.VersionMismatch => "Firmware mismatch",
            _ => "Connected",
        },
        ConnectionStatus.Disconnected => "Not connected",
        ConnectionStatus.Error => "Error",
        _ => status.ToString(),
    };

    public const string SilentText = "Nothing on this port answers as a Volco controller. Open the Controller page to check its firmware.";

    public const string MismatchText = "Controller firmware doesn't match this version of Volco. Reflash it.";

    /// <summary>"5 sliders · 3 buttons (2 LED, 1 LED pair)", while a controller answers; "" otherwise.</summary>
    public string HardwareSummary => _runtime.ControllerState == ControllerState.Ready ? Summarize(_config.Config.Controller) : "";
    public bool HasHardwareSummary => HardwareSummary.Length > 0;
    public string ControllerError => _runtime.ControllerState switch
    {
        ControllerState.VersionMismatch => MismatchText,
        ControllerState.AwaitingHello when _runtime.ControllerSilent => SilentText,
        _ => "",
    };
    public bool HasControllerError => ControllerError.Length > 0;

    internal static string Summarize(ControllerInfoConfig? c)
    {
        if (c is null) return "";
        string Plural(int n, string one) => $"{n} {one}{(n == 1 ? "" : "s")}";
        var groups = new List<string>();
        void Add(IndicatorKind kind, Func<int, string> text)
        {
            var n = c.Buttons.Count(k => k == kind);
            if (n > 0) groups.Add(text(n));
        }
        Add(IndicatorKind.Led, n => $"{n} LED");
        Add(IndicatorKind.LedPair, n => $"{n} LED pair");
        Add(IndicatorKind.Screen, n => Plural(n, "screen"));
        Add(IndicatorKind.None, n => $"{n} with no light");
        var buttons = Plural(c.Buttons.Count, "button") + (groups.Count > 0 ? $" ({string.Join(", ", groups)})" : "");
        return $"{Plural(c.Sliders, "slider")} · {buttons}";
    }

    private void OnControllerChanged(object? sender, ControllerState state) => RefreshController();
    private void OnControllerSilent(object? sender, bool silent) => RefreshController();

    private void RefreshController()
        => _ui.Post(() =>
        {
            if (_disposed) return;
            StatusText = StatusFor(Status);
            OnPropertyChanged(nameof(ControllerError));
            OnPropertyChanged(nameof(HasControllerError));
            OnPropertyChanged(nameof(FooterText));
            OnPropertyChanged(nameof(HardwareSummary));
            OnPropertyChanged(nameof(HasHardwareSummary));
        });

    private void OnControllerInfo(object? sender, EventArgs e)
        => _ui.Post(() =>
        {
            if (_disposed) return;
            OnPropertyChanged(nameof(HardwareSummary));
            OnPropertyChanged(nameof(HasHardwareSummary));
        });

    /// <summary>Starts the connection again with the current settings.</summary>
    [RelayCommand]
    private void Reconnect() => _runtime.Reconnect();

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

    // ── Serial settings ────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _comPort = "";

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
        _udpListenPort   = cfg.Udp.ListenPort;
        _webSocketPort   = cfg.WebSocket.Port;

        RefreshComPortsCommand.Execute(null);
        // A firmware install may save another port (spec §8.6): show it, without re-saving it.
        _config.ConnectionConfigChanged += OnConfigSaved;
        _config.ConfigChanged += OnConfigSaved;
        // Survives transport restarts (the runtime re-attaches its own handler to each transport
        // it creates), so unlike the old per-transport subscription this never needs re-wiring.
        _runtime.ConnectionStatusChanged += OnConnectionStatusChanged;
        _runtime.ControllerStateChanged += OnControllerChanged;
        _runtime.ControllerSilentChanged += OnControllerSilent;
        _runtime.ControllerInfoChanged += OnControllerInfo;
        UpdateStatus(_runtime.ConnectionStatus);
    }

    private void OnConfigSaved(object? sender, EventArgs e) => _ui.Post(() =>
    {
        if (_disposed) return;
        var saved = _config.Config.Connection.Serial.ComPort;
        if (saved == ComPort || string.IsNullOrEmpty(saved)) return;
        if (!ComPorts.Contains(saved)) ComPorts.Add(saved);
        ComPort = saved;   // already the saved port: OnComPortChanged neither saves nor restarts
    });

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
        // but it still leaves the property blank. Put the previous selection back when it is
        // still present; it is already the saved port, so OnComPortChanged neither saves nor
        // restarts the connection for it.
        if (!string.IsNullOrEmpty(previous) && ComPorts.Contains(previous))
            ComPort = previous;
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
        _runtime.ControllerStateChanged -= OnControllerChanged;
        _runtime.ControllerSilentChanged -= OnControllerSilent;
        _runtime.ControllerInfoChanged -= OnControllerInfo;
        _config.ConnectionConfigChanged -= OnConfigSaved;
        _config.ConfigChanged -= OnConfigSaved;
    }

    partial void OnStatusChanged(ConnectionStatus value)
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(FooterText));
    }

    private void UpdateStatus(ConnectionStatus status)
    {
        Status = status;
        StatusText = StatusFor(status);
    }

    // ── Property-change hooks — write back to config ───────────────────────────

    partial void OnConnectionTypeChanged(ConnectionType value)
    {
        _config.Config.Connection.Type = value;
        OnPropertyChanged(nameof(IsSerial));
        OnPropertyChanged(nameof(IsUdp));
        OnPropertyChanged(nameof(IsWebSocket));
        OnPropertyChanged(nameof(ConnectionTypeIndex));
        OnPropertyChanged(nameof(TransportDetails));
        OnPropertyChanged(nameof(FooterText));
        Persist();
    }

    partial void OnComPortChanged(string value)
    {
        // A null/empty port only ever arises from the bound ObservableCollection being cleared
        // during RefreshComPorts (Bug 2 - clearing ComPorts drives the two-way-bound ComboBox
        // selection, and therefore ComPort, to null) — never real user intent, since the picker
        // has no blank/"None" entry. Ignore it: no persist, no connection restart.
        if (string.IsNullOrEmpty(value)) return;
        OnPropertyChanged(nameof(TransportDetails));
        OnPropertyChanged(nameof(FooterText));
        // Already the saved port (RefreshComPorts restoring the selection the clear wiped):
        // nothing to save, and no reason to restart the connection.
        if (value == _config.Config.Connection.Serial.ComPort) return;
        _config.Config.Connection.Serial.ComPort = value;
        Persist();
    }

    partial void OnUdpListenPortChanged(int value)
    {
        _config.Config.Connection.Udp.ListenPort = value;
        OnPropertyChanged(nameof(TransportDetails));
        Persist();
    }

    partial void OnWebSocketPortChanged(int value)
    {
        _config.Config.Connection.WebSocket.Port = value;
        OnPropertyChanged(nameof(TransportDetails));
        Persist();
    }

    private void Persist() => _config.RequestSave(connectionChanged: true);
}
