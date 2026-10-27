using System.Collections.ObjectModel;
using System.Net;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Volco.Core.Runtime;

namespace Volco.App.ViewModels.Hardware;

/// <summary>A card of the Connection dialog: one transport.</summary>
public enum ConnectionCardKind { Usb, Wifi, Simulator }

/// <summary>
/// One card of the Connection dialog (hardware-page spec §10.1): its header (title, the In use badge,
/// and a one-line summary while it is closed) opens it; its body holds its settings and the one
/// button that switches to it.
/// </summary>
public abstract partial class ConnectionCardViewModel : ObservableObject
{
    private protected ConnectionCardViewModel(ConnectionDialogViewModel dialog, ConnectionCardKind kind, string title)
    {
        Dialog = dialog;
        Kind = kind;
        Title = title;
        OpenCommand = new RelayCommand(() => dialog.Open(kind));
    }

    private protected ConnectionDialogViewModel Dialog { get; }

    private protected ConnectionViewModel Connection => Dialog.Connection;

    public ConnectionCardKind Kind { get; }

    /// <summary>"USB cable", "Wi-Fi", "Simulator".</summary>
    public string Title { get; }

    /// <summary>The card is open (its body shows); one at a time.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSummary))]
    private bool _isOpen;

    /// <summary>Volco uses this card's transport now: the "In use" badge.</summary>
    public bool IsInUse => Dialog.InUse == Kind;

    /// <summary>The closed card's line: "Automatic · found the controller on COM3", "On HomeNet ·
    /// sending to 192.168.1.20", "Port 8765".</summary>
    public abstract string Summary { get; }

    public bool ShowsSummary => !IsOpen;

    /// <summary>A click on the header: opens this card, closes the others.</summary>
    public ICommand OpenCommand { get; }

    /// <summary>The connection, the controller or the Wi-Fi switch changed.</summary>
    internal virtual void Refresh()
    {
        OnPropertyChanged(nameof(IsInUse));
        OnPropertyChanged(nameof(Summary));
    }

    internal virtual void Dispose() { }

    /// <summary>Dotted IPv4 of a pc_ip.</summary>
    private protected static string FormatIp(uint ip) => new IPAddress(new[] { (byte)(ip >> 24), (byte)(ip >> 16), (byte)(ip >> 8), (byte)ip }).ToString();

    /// <summary>What a Wi-Fi operation that didn't go through says on its card.</summary>
    private protected static string? FailureText(WifiSwitchOutcome outcome, bool overWifi) => outcome switch
    {
        WifiSwitchOutcome.FellBack => WifiCardViewModel.FellBackText,
        WifiSwitchOutcome.NoAnswer => WifiCardViewModel.NoAnswerText,
        WifiSwitchOutcome.SaveFailed => "The controller didn't save the Wi-Fi settings. Try again.",
        WifiSwitchOutcome.DifferentSetup => "The controller has a different setup from Volco's copy. Sort that out on the Controller page first.",
        WifiSwitchOutcome.NeedsUsb => overWifi ? WifiCardViewModel.NotOnWifiText : WifiCardViewModel.UsbFirstText,
        _ => null,
    };

    /// <summary>Reports a <see cref="WifiSwitch"/> step to the progress dialog, on the UI thread.</summary>
    private protected sealed class StepProgress : IProgress<(WifiSwitchStep Step, TimeSpan? Left)>
    {
        private readonly ConnectionDialogViewModel _dialog;

        public StepProgress(ConnectionDialogViewModel dialog) => _dialog = dialog;

        public void Report((WifiSwitchStep Step, TimeSpan? Left) value)
            => _dialog.Ui.Post(() => _dialog.Progress?.WifiStep(Text(value.Step, value.Left)));

        internal static string Text(WifiSwitchStep step, TimeSpan? left) => step switch
        {
            WifiSwitchStep.Restarting => "Saved to the controller · restarting",
            WifiSwitchStep.WaitingForWifi => $"Waiting for the controller over Wi-Fi… {Math.Ceiling(Math.Max(0, left?.TotalSeconds ?? 0)):0} s",
            _ => "Sending…",
        };
    }
}

/// <summary>
/// The USB cable card (spec §10.2): the port list (Automatic first) with its refresh, and Switch to
/// USB. From Wi-Fi the switch asks first, inside the card, then switches at once and turns the
/// controller's Wi-Fi off at its first USB Hello (<see cref="WifiSwitch.TurnOffAsync"/>, in the
/// progress dialog while this dialog is open); from the simulator it switches at once.
/// </summary>
public sealed partial class UsbCardViewModel : ConnectionCardViewModel
{
    private bool _hasPendingPort;
    private string? _pendingPort;   // the port picked while the card isn't in use; null is Automatic
    private Task<WifiSwitchOutcome>? _turnOff;
    private bool _sawPending;
    private bool _showsProgress;
    private bool _disposed;

    internal UsbCardViewModel(ConnectionDialogViewModel dialog) : base(dialog, ConnectionCardKind.Usb, "USB cable")
    {
        SwitchCommand = new RelayCommand(Switch, () => !IsInUse && !ConfirmingWifiOff);
        ConfirmSwitchCommand = new RelayCommand(ConfirmSwitch);
        CancelSwitchCommand = new RelayCommand(() => ConfirmingWifiOff = false);
    }

    /// <summary>Automatic (the port it uses, when it found one), then each port with its description.</summary>
    public ObservableCollection<PortChoice> Ports => Connection.PortChoices;

    /// <summary>The port list's refresh button.</summary>
    public ICommand RefreshCommand => Connection.RefreshComPortsCommand;

    /// <summary>The picked port. In use, it is Volco's port at once; otherwise it waits for Switch to USB.
    /// A null (the list clearing during a refresh) is ignored.</summary>
    public PortChoice? SelectedPort
    {
        get => IsInUse || !_hasPendingPort ? Connection.SelectedPort : Ports.FirstOrDefault(p => p.Port == _pendingPort);
        set
        {
            if (value is null) return;
            if (IsInUse) Connection.SelectedPort = value;
            else
            {
                _pendingPort = value.Port;
                _hasPendingPort = true;
            }
            OnPropertyChanged();
        }
    }

    public override string Summary => Connection.ComPort is { Length: > 0 } port ? port
        : Connection.UsbPort is { } found ? $"Automatic · found the controller on {found}" : "Automatic";

    /// <summary>Switch to USB (not shown in the card in use).</summary>
    public IRelayCommand SwitchCommand { get; }

    public string SwitchLabel => "Switch to USB";

    /// <summary>Switch to USB from Wi-Fi asks first: "Plug the controller in, then switch. Volco
    /// turns its Wi-Fi off."</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSwitch))]
    [NotifyCanExecuteChangedFor(nameof(SwitchCommand))]
    private bool _confirmingWifiOff;

    public string WifiOffQuestion => "Plug the controller in, then switch. Volco turns its Wi-Fi off.";

    public bool ShowsSwitch => !IsInUse && !ConfirmingWifiOff;

    /// <summary>"Switch to USB and turn Wi-Fi off".</summary>
    public IRelayCommand ConfirmSwitchCommand { get; }

    public IRelayCommand CancelSwitchCommand { get; }

    /// <summary>Volco is on USB and turns the controller's Wi-Fi off once it answers there.</summary>
    public bool TurnOffPending => Dialog.WifiSwitch?.TurnOffPending == true;

    /// <summary>The last turn-off's save didn't go through; null otherwise.</summary>
    [ObservableProperty] private string? _error;

    private void Switch()
    {
        if (IsInUse) return;
        if (Connection.ConnectionType == ConnectionType.Udp && Dialog.WifiSwitch is not null)
        {
            ConfirmingWifiOff = true;
            return;
        }
        ApplyPort();
        Connection.ConnectionType = ConnectionType.Serial;
        Dialog.TransportEdited();
    }

    private void ApplyPort()
    {
        if (_hasPendingPort) Connection.ComPort = _pendingPort ?? "";
        _hasPendingPort = false;
    }

    private void ConfirmSwitch()
    {
        ConfirmingWifiOff = false;
        if (IsInUse || Dialog.WifiSwitch is not { } wifi) return;
        Error = null;
        ApplyPort();
        _sawPending = false;
        _showsProgress = false;
        var turnOff = wifi.TurnOffAsync();
        _turnOff = turnOff;
        // The switch to USB is posted to the config thread (the UI thread) by now: show it after it.
        Dialog.Ui.Post(Dialog.TransportEdited);
        _ = turnOff.ContinueWith(t => Dialog.Ui.Post(() => TurnedOff(t.Result)), TaskScheduler.Default);
    }

    internal override void Refresh()
    {
        base.Refresh();
        OnPropertyChanged(nameof(SelectedPort));
        OnPropertyChanged(nameof(ShowsSwitch));
        OnPropertyChanged(nameof(TurnOffPending));
        SwitchCommand.NotifyCanExecuteChanged();
        if (IsInUse && ConfirmingWifiOff) ConfirmingWifiOff = false;
        WatchTurnOff();
    }

    /// <summary>The turn-off's save starts once the USB Hello came (the pending wait ended while the
    /// operation still runs): it shows in the progress dialog while this dialog is open.</summary>
    private void WatchTurnOff()
    {
        if (_disposed || _turnOff is not { IsCompleted: false } || Dialog.WifiSwitch is not { } wifi) return;
        if (wifi.TurnOffPending)
        {
            _sawPending = true;
            return;
        }
        if (!_sawPending || _showsProgress || !wifi.IsBusy) return;
        // Already off in Volco's copy: nothing is saved.
        if (WifiSwitch.FromMirror(Dialog.Controller.Mirror, 0) is null) return;
        _showsProgress = true;
        Dialog.Progress?.BeginWifi("Turning Wi-Fi off", "Saving to the controller…");
    }

    private void TurnedOff(WifiSwitchOutcome outcome)
    {
        var shown = _showsProgress;
        _showsProgress = false;
        _turnOff = null;
        if (shown) Dialog.Progress?.EndWifi(outcome == WifiSwitchOutcome.BackOnUsb ? "Saved. The controller's Wi-Fi is off." : null);
        if (_disposed) return;
        Error = FailureText(outcome, overWifi: false);
        Dialog.TransportEdited();
    }

    internal override void Dispose() => _disposed = true;
}

/// <summary>
/// The Wi-Fi card (spec §10.3): the controller's Wi-Fi settings, filled from its saved setup
/// (Network, Password with Show, This PC's address, and under Ports › Volco's listen port and the
/// controller's port), checked as they are typed, and one button: Save and switch from USB
/// (<see cref="WifiSwitch.SaveAndSwitchAsync"/>) or Save on Wi-Fi (<see cref="WifiSwitch.SaveOverWifiAsync"/>).
/// </summary>
public sealed partial class WifiCardViewModel : ConnectionCardViewModel
{
    public const string UsbFirstText = "Connect the controller over USB first; the Wi-Fi settings are sent through it.";
    public const string NoAnswerText = "The controller didn't answer over Wi-Fi. Check the network name and password.";
    public const string FellBackText = "The controller couldn't use the new Wi-Fi settings and went back to the old ones. Check the network name and password.";
    internal const string NotOnWifiText = "The controller isn't connected over Wi-Fi.";

    private string? _problem;   // the fields' problems, from the validator
    private string? _outcome;   // the last save's failure
    private bool _disposed;

    internal WifiCardViewModel(ConnectionDialogViewModel dialog, IReadOnlyList<LocalAddress> addresses)
        : base(dialog, ConnectionCardKind.Wifi, "Wi-Fi")
    {
        var listenPort = dialog.Connection.UdpListenPort;
        var saved = WifiSwitch.FromMirror(MirrorBytes(), listenPort);
        _ssid = saved?.Ssid ?? "";
        _password = saved?.Password ?? "";
        _listenPort = listenPort;
        _controllerPort = saved?.ControllerPort ?? (int)new Udp().LocalPort;
        // The saved address while this PC still has it, else the first.
        Addresses = addresses;
        _selectedAddress = addresses.FirstOrDefault(a => saved is not null && a.Value == saved.PcIp) ?? addresses.FirstOrDefault();
        SaveCommand = new RelayCommand(Save, () => CanSave);
        TogglePortsCommand = new RelayCommand(() => PortsOpen = !PortsOpen);
        Validate();
    }

    /// <summary>The network's name (SSID).</summary>
    [ObservableProperty] private string _ssid;

    [ObservableProperty] private string _password;

    /// <summary>The password shows as typed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordChar))]
    private bool _showPassword;

    public char PasswordChar => ShowPassword ? '\0' : '•';

    /// <summary>This PC's IPv4 addresses, each with its adapter.</summary>
    public IReadOnlyList<LocalAddress> Addresses { get; }

    /// <summary>The address the controller sends to.</summary>
    [ObservableProperty] private LocalAddress? _selectedAddress;

    /// <summary>Ports › is open.</summary>
    [ObservableProperty] private bool _portsOpen;

    public ICommand TogglePortsCommand { get; }

    /// <summary>The port Volco listens on (<c>connection.udp.listenPort</c>), also the controller's PC port.</summary>
    [ObservableProperty] private decimal? _listenPort;

    /// <summary>The controller's own port.</summary>
    [ObservableProperty] private decimal? _controllerPort;

    partial void OnSsidChanged(string value) => Edited();
    partial void OnPasswordChanged(string value) => Edited();
    partial void OnSelectedAddressChanged(LocalAddress? value) => Edited();
    partial void OnListenPortChanged(decimal? value) => Edited();
    partial void OnControllerPortChanged(decimal? value) => Edited();

    private void Edited()
    {
        _outcome = null;
        Validate();
    }

    /// <summary>Volco is on Wi-Fi: the save goes over Wi-Fi and the button reads Save.</summary>
    private bool OnWifi => Connection.ConnectionType == ConnectionType.Udp;

    /// <summary>"Save and switch" (Volco on USB) or "Save" (already on Wi-Fi).</summary>
    public string ButtonLabel => OnWifi ? "Save" : "Save and switch";

    /// <summary>"Sent over USB, then Volco switches to Wi-Fi." shows under the fields (not on Wi-Fi).</summary>
    public bool ShowsNote => !OnWifi;

    /// <summary>Why the button is disabled although the fields are fine; null otherwise.</summary>
    public string? DisabledReason
    {
        get
        {
            if (Dialog.WifiSwitch is not { } wifi || wifi.IsBusy) return null;
            if (MirrorBytes() is null) return "Set up the controller first; its Wi-Fi settings are saved with its setup.";
            if (OnWifi) return Connection.IsConnected ? null : NotOnWifiText;
            return Connection.ConnectionType == ConnectionType.Serial && Connection.IsConnected ? null : UsbFirstText;
        }
    }

    public bool HasDisabledReason => DisabledReason is not null;

    /// <summary>The fields' problems, else the last save's failure; null for none.</summary>
    public string? Error => _problem ?? _outcome;

    public bool HasError => Error is not null;

    /// <summary>The button is enabled: the fields are valid and the controller can be reached.</summary>
    public bool CanSave => Dialog.WifiSwitch is { IsBusy: false } && _problem is null && DisabledReason is null;

    public IRelayCommand SaveCommand { get; }

    public override string Summary => WifiSwitch.FromMirror(MirrorBytes(), 0) is { } s
        ? $"On {s.Ssid} · sending to {FormatIp(s.PcIp)}" : "Off";

    private byte[]? MirrorBytes() => Dialog.Controller.Mirror;

    private WifiSettings Settings() => new(Ssid ?? "", Password ?? "", SelectedAddress?.Value ?? 0,
                                           (int)(ListenPort ?? 0), (int)(ControllerPort ?? 0));

    /// <summary>The controller's saved setup with these Wi-Fi settings, through the controller's own
    /// rules: the problems that come with them (the Wi-Fi fields', and a slider on a pin Wi-Fi
    /// blocks) are the card's.</summary>
    private void Validate()
    {
        _problem = null;
        if (Dialog.Controller.DecodeMirror() is { } config)
        {
            var before = DraftProblems.Find(config, ChipInfo.Esp32).Select(p => (p.Field, p.Item)).ToHashSet();
            var s = Settings();
            config.Udp = new Udp
            {
                Enabled = true, Ssid = s.Ssid, Password = s.Password, PcIp = s.PcIp,
                PcPort = (uint)Math.Max(0, s.ListenPort), LocalPort = (uint)Math.Max(0, s.ControllerPort),
            };
            var problems = DraftProblems.Find(config, ChipInfo.Esp32).Where(p => !before.Contains((p.Field, p.Item))).Select(p => p.Message).ToList();
            if (problems.Count > 0) _problem = string.Join("\n", problems);
        }
        RaiseState();
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(DisabledReason));
        OnPropertyChanged(nameof(HasDisabledReason));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ButtonLabel));
        OnPropertyChanged(nameof(ShowsNote));
        SaveCommand.NotifyCanExecuteChanged();
    }

    // The saved setup the fields are checked against may have changed too.
    internal override void Refresh()
    {
        base.Refresh();
        if (!_disposed) Validate();
    }

    private void Save()
    {
        if (!CanSave || Dialog.WifiSwitch is not { } wifi) return;
        var overWifi = OnWifi;
        _outcome = null;
        var progress = new StepProgress(Dialog);
        Dialog.Progress?.BeginWifi("Saving to the controller", StepProgress.Text(WifiSwitchStep.Saving, null));
        var run = overWifi ? wifi.SaveOverWifiAsync(Settings(), progress) : wifi.SaveAndSwitchAsync(Settings(), progress);
        RaiseState();
        _ = run.ContinueWith(t => Dialog.Ui.Post(() => Saved(t.Result, overWifi)), TaskScheduler.Default);
    }

    private void Saved(WifiSwitchOutcome outcome, bool overWifi)
    {
        Dialog.Progress?.EndWifi(outcome == WifiSwitchOutcome.OnWifi ? "Saved. Volco is on Wi-Fi." : null);
        if (_disposed) return;
        _outcome = FailureText(outcome, overWifi);
        Dialog.TransportEdited();
        if (outcome == WifiSwitchOutcome.OnWifi) IsOpen = false;   // In use, closed with its summary
        RaiseState();
    }

    internal override void Dispose() => _disposed = true;
}

/// <summary>The Simulator card (spec §10.4): its WebSocket port and Switch to the simulator.</summary>
public sealed partial class SimulatorCardViewModel : ConnectionCardViewModel
{
    private decimal? _pendingPort;   // the port typed while the card isn't in use

    internal SimulatorCardViewModel(ConnectionDialogViewModel dialog) : base(dialog, ConnectionCardKind.Simulator, "Simulator")
    {
        SwitchCommand = new RelayCommand(Switch, () => !IsInUse);
    }

    /// <summary>The port the simulator listens on. In use, it is Volco's port at once; otherwise it
    /// waits for Switch to the simulator.</summary>
    public decimal? Port
    {
        get => IsInUse || _pendingPort is null ? Connection.WebSocketPort : _pendingPort;
        set
        {
            if (value is not { } port) return;
            if (IsInUse) Connection.WebSocketPort = (int)port;
            else _pendingPort = port;
            OnPropertyChanged();
        }
    }

    public override string Summary => $"Port {Connection.WebSocketPort}";

    /// <summary>Switch to the simulator (not shown in the card in use). A pending turn-off of the
    /// controller's Wi-Fi is dropped (<see cref="WifiSwitch.TurnOffAsync"/>).</summary>
    public IRelayCommand SwitchCommand { get; }

    private void Switch()
    {
        if (IsInUse) return;
        if (_pendingPort is { } port) Connection.WebSocketPort = (int)port;
        _pendingPort = null;
        Connection.ConnectionType = ConnectionType.WebSocket;
        Dialog.TransportEdited();
    }

    internal override void Refresh()
    {
        base.Refresh();
        OnPropertyChanged(nameof(Port));
        SwitchCommand.NotifyCanExecuteChanged();
    }
}
