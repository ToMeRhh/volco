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

    // LinkStatusChanged can fire on any thread; the handler marshals through this.
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

    // ── Live status: only the runtime's link state (never the transport's) ───────

    /// <summary>The link state the panel shows; read on the UI thread when it changes.</summary>
    [ObservableProperty]
    private LinkStatus _link = LinkStatus.Disconnected;

    public bool IsConnected => Link.IsConnected;

    /// <summary>The status headline: "Connected", "Controller not responding", …</summary>
    public string StatusText => LinkText.Headline(Link, _config.Config.Connection);

    /// <summary>The dot's colour (and the explanation's).</summary>
    public LinkTone StatusTone => LinkText.Tone(Link.State);

    /// <summary>What the status panel says next to the state, e.g. "COM3 · USB cable", "192.168.1.40 · Wi-Fi".</summary>
    public string TransportDetails => LinkText.Transport(_config.Config.Connection, Link.IsConnected ? _runtime.PeerAddress : null);

    /// <summary>"5 sliders · 3 buttons (2 LED, 1 LED pair)", while connected; "" otherwise.</summary>
    public string HardwareSummary => Link.IsConnected ? Summarize(_config.Config.Controller) : "";
    public bool HasHardwareSummary => HardwareSummary.Length > 0;

    /// <summary>What a status other than Connected means and what to do about it; "" when connected.</summary>
    public string StatusExplanation => LinkText.Explanation(Link, _config.Config.Connection);
    public bool HasStatusExplanation => StatusExplanation.Length > 0;

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

    // LinkStatusChanged and ControllerInfoChanged fire on any thread: post, then read the
    // runtime's current state on the UI thread (a later event can't be overtaken by an earlier one).
    private void OnRuntimeLinkChanged(object? sender, LinkStatus link)
        => _ui.Post(() =>
        {
            if (!_disposed) Link = _runtime.LinkStatus;
        });

    private void OnControllerInfo(object? sender, EventArgs e)
        => _ui.Post(() =>
        {
            if (!_disposed) RefreshStatus();
        });

    partial void OnLinkChanged(LinkStatus value) => RefreshStatus();

    private void RefreshStatus()
    {
        foreach (var name in new[] { nameof(IsConnected), nameof(IsUserDisconnected), nameof(StatusText), nameof(StatusTone),
                                     nameof(TransportDetails), nameof(HardwareSummary),
                                     nameof(HasHardwareSummary), nameof(StatusExplanation), nameof(HasStatusExplanation) })
            OnPropertyChanged(name);
    }

    /// <summary>Starts the connection again with the current settings.</summary>
    [RelayCommand]
    private void Reconnect() => _runtime.Reconnect();

    /// <summary>Drops the connection until <see cref="ConnectCommand"/>.</summary>
    [RelayCommand]
    private void Disconnect() => _runtime.Disconnect();

    /// <summary>Ends a Disconnect.</summary>
    [RelayCommand]
    private void Connect() => _runtime.Connect();

    /// <summary>True after Disconnect, until Connect or Reconnect: the page offers Connect.</summary>
    public bool IsUserDisconnected => Link.State == LinkState.Disconnected;

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
        // Survives transport restarts: the runtime derives the link state across them.
        _runtime.LinkStatusChanged += OnRuntimeLinkChanged;
        _runtime.ControllerInfoChanged += OnControllerInfo;
        _link = _runtime.LinkStatus;
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

    /// <summary>Unsubscribes from the runtime (carried rule d).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runtime.LinkStatusChanged -= OnRuntimeLinkChanged;
        _runtime.ControllerInfoChanged -= OnControllerInfo;
        _config.ConnectionConfigChanged -= OnConfigSaved;
        _config.ConfigChanged -= OnConfigSaved;
    }

    // ── Property-change hooks — write back to config ───────────────────────────

    partial void OnConnectionTypeChanged(ConnectionType value)
    {
        _config.Config.Connection.Type = value;
        OnPropertyChanged(nameof(IsSerial));
        OnPropertyChanged(nameof(IsUdp));
        OnPropertyChanged(nameof(IsWebSocket));
        OnPropertyChanged(nameof(ConnectionTypeIndex));
        RefreshStatus();
        Persist();
    }

    partial void OnComPortChanged(string value)
    {
        // A null/empty port only ever arises from the bound ObservableCollection being cleared
        // during RefreshComPorts (Bug 2 - clearing ComPorts drives the two-way-bound ComboBox
        // selection, and therefore ComPort, to null) — never real user intent, since the picker
        // has no blank/"None" entry. Ignore it: no persist, no connection restart.
        if (string.IsNullOrEmpty(value)) return;
        // Already the saved port (RefreshComPorts restoring the selection the clear wiped):
        // nothing to save, and no reason to restart the connection.
        if (value == _config.Config.Connection.Serial.ComPort)
        {
            RefreshStatus();
            return;
        }
        _config.Config.Connection.Serial.ComPort = value;
        RefreshStatus();
        Persist();
    }

    partial void OnUdpListenPortChanged(int value)
    {
        _config.Config.Connection.Udp.ListenPort = value;
        RefreshStatus();
        Persist();
    }

    partial void OnWebSocketPortChanged(int value)
    {
        _config.Config.Connection.WebSocket.Port = value;
        RefreshStatus();
        Persist();
    }

    private void Persist() => _config.RequestSave(connectionChanged: true);
}
