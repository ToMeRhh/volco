using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Runtime;

namespace Volco.App.ViewModels;

/// <summary>One entry of the port list; <c>Port</c> null is Automatic.</summary>
public sealed record PortChoice(string? Port, string Label);

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
    public string StatusText => LinkText.Headline(Link, _config.Config.Connection, UsbPort);

    /// <summary>The USB port in use (<see cref="VolcoRuntime.UsbPort"/>): the chosen one, or the one
    /// Automatic found; null for none.</summary>
    public string? UsbPort => _runtime.UsbPort;

    /// <summary>The dot's colour (and the explanation's).</summary>
    public LinkTone StatusTone => LinkText.Tone(Link.State);

    /// <summary>What the status panel says next to the state, e.g. "COM3 · USB cable", "192.168.1.40 · Wi-Fi".</summary>
    public string TransportDetails => LinkText.Transport(_config.Config.Connection, Link.IsConnected ? _runtime.PeerAddress : null, UsbPort);

    /// <summary>"5 sliders · 3 buttons (2 LED, 1 LED pair)", while connected; "" otherwise.</summary>
    public string HardwareSummary => Link.IsConnected ? Summarize(_config.Config.Controller) : "";
    public bool HasHardwareSummary => HardwareSummary.Length > 0;

    /// <summary>What a status other than Connected means and what to do about it; "" when connected.</summary>
    public string StatusExplanation => LinkText.Explanation(Link, _config.Config.Connection, UsbPort);
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

    // UsbPortChanged fires on any thread too.
    private void OnRuntimeUsbPortChanged(object? sender, EventArgs e)
        => _ui.Post(() =>
        {
            if (_disposed) return;
            RefreshStatus();
            RefreshAutomaticLabel();
        });

    private void OnControllerInfo(object? sender, EventArgs e)
        => _ui.Post(() =>
        {
            if (!_disposed) RefreshStatus();
        });

    partial void OnLinkChanged(LinkStatus value) => RefreshStatus();

    private void RefreshStatus()
    {
        foreach (var name in new[] { nameof(IsConnected), nameof(IsUserDisconnected), nameof(UsbPort), nameof(StatusText), nameof(StatusTone),
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

    /// <summary>The port list: Automatic first, then the system's ports (with their descriptions
    /// where it has them), and the chosen port when it isn't plugged in.</summary>
    public ObservableCollection<PortChoice> PortChoices { get; } = new();

    /// <summary>The picked entry of <see cref="PortChoices"/>; setting it writes <see cref="ComPort"/>
    /// ("" for Automatic). A null (the ComboBox clearing during a refresh) is ignored.</summary>
    public PortChoice? SelectedPort
    {
        get => PortChoices.FirstOrDefault(p => (p.Port ?? "") == ComPort);
        set { if (value is not null) ComPort = value.Port ?? ""; }
    }

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
        _runtime.UsbPortChanged += OnRuntimeUsbPortChanged;
        _link = _runtime.LinkStatus;
    }

    private void OnConfigSaved(object? sender, EventArgs e) => _ui.Post(() =>
    {
        if (!_disposed) SyncFromConfig();
    });

    /// <summary>True while <see cref="SyncFromConfig"/> sets the properties: nothing is written back.</summary>
    private bool _syncing;

    /// <summary>Shows the connection config as it is now, without saving it again: a firmware install
    /// may save another port (spec §8.6), and the Connection dialog's Wi-Fi switch edits the
    /// transport and the listen port itself (spec §10.2, §10.3). UI thread.</summary>
    internal void SyncFromConfig()
    {
        var c = _config.Config.Connection;
        var saved = c.Serial.ComPort;
        if (saved != ComPort)
        {
            if (saved != "" && !PortChoices.Any(p => p.Port == saved)) PortChoices.Add(new PortChoice(saved, saved));
            ComPort = saved;   // already the saved port: OnComPortChanged neither saves nor restarts
        }
        _syncing = true;
        try
        {
            ConnectionType = c.Type;
            UdpListenPort = c.Udp.ListenPort;
            WebSocketPort = c.WebSocket.Port;
        }
        finally { _syncing = false; }
    }

    // ── COM-port refresh command ───────────────────────────────────────────────

    [RelayCommand]
    private void RefreshComPorts()
    {
        PortChoices.Clear();
        PortChoices.Add(new PortChoice(null, AutomaticLabel()));
        foreach (var port in _runtime.ListSerialPorts())
            PortChoices.Add(new PortChoice(port.Name, PortLabel(port)));
        // The chosen port stays listed while it is unplugged.
        if (ComPort != "" && !PortChoices.Any(p => p.Port == ComPort))
            PortChoices.Add(new PortChoice(ComPort, ComPort));

        // PortChoices.Clear() drives the two-way-bound ComboBox.SelectedItem — and therefore
        // SelectedPort — to null; the setter ignores that. Announce the selection again so the
        // ComboBox picks the saved entry back up (ComPort itself never changed).
        OnPropertyChanged(nameof(SelectedPort));
    }

    private string AutomaticLabel() => UsbPort is { } port && string.IsNullOrEmpty(_config.Config.Connection.Serial.ComPort)
        ? $"Automatic ({port})" : "Automatic";

    /// <summary>A port's list entry: its name and the system's description, "COM3 · USB-SERIAL CH340".</summary>
    internal static string PortLabel(SerialPortDescription port)
        => string.IsNullOrEmpty(port.Description) ? port.Name : $"{port.Name} · {port.Description}";

    // Automatic's label names the port it found; only that entry changes.
    private void RefreshAutomaticLabel()
    {
        if (PortChoices.Count == 0 || PortChoices[0].Port is not null) return;
        var label = AutomaticLabel();
        if (PortChoices[0].Label == label) return;
        PortChoices[0] = new PortChoice(null, label);
        OnPropertyChanged(nameof(SelectedPort));
    }

    /// <summary>Unsubscribes from the runtime (carried rule d).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runtime.LinkStatusChanged -= OnRuntimeLinkChanged;
        _runtime.ControllerInfoChanged -= OnControllerInfo;
        _runtime.UsbPortChanged -= OnRuntimeUsbPortChanged;
        _config.ConnectionConfigChanged -= OnConfigSaved;
        _config.ConfigChanged -= OnConfigSaved;
    }

    // ── Property-change hooks — write back to config ───────────────────────────

    partial void OnConnectionTypeChanged(ConnectionType value)
    {
        OnPropertyChanged(nameof(IsSerial));
        OnPropertyChanged(nameof(IsUdp));
        OnPropertyChanged(nameof(IsWebSocket));
        OnPropertyChanged(nameof(ConnectionTypeIndex));
        RefreshStatus();
        Write(c => c.Type = value);
    }

    private void Write(Action<ConnectionConfig> edit)
    {
        if (_syncing) return;
        edit(_config.Config.Connection);
        Persist();
    }

    partial void OnComPortChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedPort));
        // Already the saved port ("" is Automatic): nothing to save, and no reason to restart the connection.
        if (value == _config.Config.Connection.Serial.ComPort)
        {
            RefreshStatus();
            return;
        }
        _config.Config.Connection.Serial.ComPort = value;
        RefreshStatus();
        RefreshAutomaticLabel();
        Persist();
    }

    partial void OnUdpListenPortChanged(int value)
    {
        RefreshStatus();
        Write(c => c.Udp.ListenPort = value);
    }

    partial void OnWebSocketPortChanged(int value)
    {
        RefreshStatus();
        Write(c => c.WebSocket.Port = value);
    }

    private void Persist() => _config.RequestSave(connectionChanged: true);
}
