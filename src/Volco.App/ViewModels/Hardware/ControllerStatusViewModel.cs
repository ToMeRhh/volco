using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Runtime;

namespace Volco.App.ViewModels.Hardware;

/// <summary>
/// The controller's status in the settings window, on every page (controller-page spec §5;
/// hardware-page spec §4.1's states); its <see cref="Level"/> decides where it shows. The quiet
/// line at the bottom of the sidebar always shows (a dot and <see cref="ShortText"/>, with the menu
/// of the rare actions, §4.2); when the status needs the user, the banner across the top of the
/// content area shows too (the dot, <see cref="FullText"/> and at most one primary button,
/// <see cref="DeviceCard"/>). It reads the page's <see cref="ControllerViewModel"/> and the shared
/// <see cref="ConnectionViewModel"/>, so it agrees with the tray. The actions that belong to the
/// drawing raise <see cref="RequestController"/> first. UI thread.
/// </summary>
public sealed partial class ControllerStatusViewModel : ObservableObject, IDisposable
{
    private readonly ControllerViewModel _controller;
    private readonly HardwareViewModel _hardware;
    private bool _disposed;

    public ControllerStatusViewModel(ControllerViewModel controller, HardwareViewModel hardware)
    {
        _controller = controller;
        _hardware = hardware;
        _controller.PropertyChanged += OnSourceChanged;
        _controller.Connection.PropertyChanged += OnSourceChanged;
        var inputs = Inputs();
        _state = DeviceCard.From(inputs);
        _level = DeviceCard.LevelOf(inputs, _state);
        _shortText = DeviceCard.ShortStatus(inputs, _state, _level);
        Progress = new ProgressDialogViewModel(controller, ToController);
        Progress.PropertyChanged += OnProgressChanged;
        UpdateAttention(inputs);
    }

    /// <summary>The progress dialog: a running operation, then its result (spec §5's third level).</summary>
    public ProgressDialogViewModel Progress { get; }

    private void OnProgressChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProgressDialogViewModel.IsOpen)) return;
        OnPropertyChanged(nameof(ShowsBanner));
        OnPropertyChanged(nameof(IsAttentionOpen));
    }

    // ── The attention dialog (controller-page spec §5) ───────────────────────────────────────

    private AttentionKind _attention;

    /// <summary>A state that makes the controller unusable (no controller answers, firmware too
    /// old) began and the user hasn't answered its dialog yet. Set once each time such a state
    /// begins; cleared by Not now, by the dialog's action, and when the state ends.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAttentionOpen))]
    private bool _attentionPending;

    /// <summary>The attention dialog shows: the status as its title, the detail, the status's button
    /// and Not now. The progress dialog, above it, takes over while it is open. The window shows it
    /// whenever it is itself shown, so a state that began while it was hidden shows on opening.</summary>
    public bool IsAttentionOpen => AttentionPending && !Progress.IsOpen;

    /// <summary>Which attention state the status is in, the last time it was in one or none (the
    /// expected waits keep it).</summary>
    public AttentionKind Attention => _attention;

    private void UpdateAttention(CardInputs inputs)
    {
        if (DeviceCard.AttentionOf(inputs) is not { } now || now == _attention) return;
        _attention = now;
        // A state that begins under the progress dialog was told there (the operation's result).
        AttentionPending = now != AttentionKind.None && !Progress.IsOpen;
    }

    /// <summary>Not now (Escape and the scrim too): the dialog closes and the banner stays; it opens
    /// again only after the state ends and begins again.</summary>
    [RelayCommand]
    private void NotNow() => AttentionPending = false;

    /// <summary>The dialog's button: the status's action (Check firmware, Update). An operation it
    /// starts shows in the progress dialog.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private void AttentionAct()
    {
        if (!CanAct) return;
        AttentionPending = false;
        Action();
    }

    /// <summary>The page's controller logic: the Install confirmation binds to it.</summary>
    public ControllerViewModel Controller => _controller;

    private ConnectionViewModel Connection => _controller.Connection;

    public string Name => DeviceCard.Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionLabel), nameof(HasAction), nameof(FullText))]
    private CardState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsBanner))]
    private StatusLevel _level;

    /// <summary>The sidebar line's words (<see cref="DeviceCard.ShortStatus"/>): "Connected" or
    /// "Connecting…" when all is fine, a word or two for what needs the user, the step while an
    /// operation runs. <see cref="FullText"/> is its tooltip.</summary>
    [ObservableProperty] private string _shortText;

    /// <summary>The connected controller's hardware ("ESP32 · 5 sliders, 4 buttons"), under the
    /// sidebar's status line; "" while not connected.</summary>
    public string HardwareText => _controller.HardwareText;

    /// <summary>The status and its detail on one line: the banner's text and the sidebar line's tooltip.</summary>
    public string FullText => State.Detail is { } detail ? $"{State.Status} · {detail}" : State.Status;

    /// <summary>The banner shows while the status needs the user, except under the progress dialog,
    /// which shows the running operation and then its result; after Close, what remains shows here.</summary>
    public bool ShowsBanner => Level != StatusLevel.Fine && !Progress.IsOpen;

    /// <summary>Raised before an action that belongs to the drawing acts (Fix it, Test your
    /// controller now, Load a preset…, Start over…, the not-set-up states' chooser): the window
    /// shows the Controller page first (controller-page spec §5).</summary>
    public event EventHandler? RequestController;

    private void ToController() => RequestController?.Invoke(this, EventArgs.Empty);

    public string ActionLabel => DeviceCard.Label(State.Action);
    public bool HasAction => State.Action != CardAction.None;

    /// <summary>The inputs of <see cref="DeviceCard.From"/>, read from the page now.</summary>
    internal CardInputs Inputs()
    {
        var c = _controller;
        var type = Connection.ConnectionType;
        var config = c.ConnectionConfig;
        var port = type switch
        {
            ConnectionType.Serial => string.IsNullOrEmpty(Connection.ComPort) ? null : Connection.ComPort,
            ConnectionType.Udp => config.Udp.ListenPort.ToString(),
            _ => config.WebSocket.Port.ToString(),
        };
        var running = c.IsFirmwareRunning;
        return new CardInputs(Connection.Link, c.Banner, type, port,
                              running ? LinkText.Activity(c.FirmwareActivity) : null,
                              running && c.FirmwareActivity != LinkActivity.CheckingFirmware ? c.FirmwareProgress : null,
                              c.SavePhaseText, c.Failure, c.OfferTest, c.OfferInstall, c.OfferFreshInstall);
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        var inputs = Inputs();
        State = DeviceCard.From(inputs);
        Level = DeviceCard.LevelOf(inputs, State);
        ShortText = DeviceCard.ShortStatus(inputs, State, Level);
        OnPropertyChanged(nameof(HardwareText));
        UpdateAttention(inputs);
        ActionCommand.NotifyCanExecuteChanged();
        AttentionActCommand.NotifyCanExecuteChanged();
        CheckFirmwareCommand.NotifyCanExecuteChanged();
        ReinstallFirmwareCommand.NotifyCanExecuteChanged();
        LoadPresetCommand.NotifyCanExecuteChanged();
        StartOverCommand.NotifyCanExecuteChanged();
        if (IsPresetChooserOpen && !CanLoadPreset) IsPresetChooserOpen = false;
    }

    // ── The primary button ───────────────────────────────────────────────────────────────────

    private bool CanAct => State.Action switch
    {
        CardAction.None => false,
        CardAction.Connect => true,
        CardAction.Retry => Connection.Link.State == LinkState.PortBusy || !_controller.Busy,
        CardAction.CheckFirmware or CardAction.Update => _controller.CanUseFirmware,
        CardAction.TestNow => _controller.CanStartTest,
        CardAction.Install or CardAction.Reinstall => _controller.CanUseFirmware && !_controller.Busy,
        CardAction.ChooseHowToStart => true,
        _ => !_controller.Busy,
    };

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Action()
    {
        if (!CanAct) return;
        switch (State.Action)
        {
            case CardAction.Connect: Connection.ConnectCommand.Execute(null); break;
            case CardAction.Retry when Connection.Link.State == LinkState.PortBusy: Connection.ReconnectCommand.Execute(null); break;
            case CardAction.Retry: _controller.RetryCommand.Execute(null); break;
            case CardAction.CheckFirmware: _controller.CheckFirmwareCommand.Execute(null); break;
            case CardAction.Update: _controller.UpdateFirmwareCommand.Execute(null); break;
            case CardAction.FixIt: ToController(); _controller.FixItCommand.Execute(null); break;
            case CardAction.Compare: _hardware.OpenCompare(); break;
            case CardAction.TestNow: ToController(); _controller.TestNowCommand.Execute(null); break;
            case CardAction.Install or CardAction.Reinstall: _controller.InstallFirmwareCommand.Execute(null); break;   // asks first
            case CardAction.ChooseHowToStart: ToController(); break;   // the chooser is on the Controller page
        }
    }

    /// <summary>The menu's Connection…: the Connection dialog, on the tab of the current connection.</summary>
    [RelayCommand]
    private void OpenConnection() => _hardware.OpenConnection(wifi: Connection.ConnectionType == ConnectionType.Udp);

    // ── The status line's menu (spec §4.2) ────────────────────────────────────────────────────

    [RelayCommand]
    private void Reconnect() => Connection.ReconnectCommand.Execute(null);

    [RelayCommand]
    private void Disconnect() => Connection.DisconnectCommand.Execute(null);

    /// <summary>Items that write to the controller wait while an operation or a test runs.</summary>
    private bool CanUseFirmware => _controller.CanUseFirmware && !_controller.Busy;

    [RelayCommand(CanExecute = nameof(CanUseFirmware))]
    private void CheckFirmware()
    {
        if (CanUseFirmware) _controller.CheckFirmwareCommand.Execute(null);
    }

    /// <summary>Today's Install: it asks first (<see cref="ControllerViewModel.ConfirmInstallPending"/>),
    /// since it erases the controller's setup.</summary>
    [RelayCommand(CanExecute = nameof(CanUseFirmware))]
    private void ReinstallFirmware()
    {
        if (CanUseFirmware) _controller.InstallFirmwareCommand.Execute(null);
    }

    private bool CanLoadPreset => _controller.CanEdit && !_controller.Busy;

    /// <summary>The presets "Load a preset…" offers.</summary>
    public IReadOnlyList<ControllerPreset> Presets { get; } = ControllerPresets.All.Where(p => p.IsAvailable).ToList();

    /// <summary>The small preset chooser is open (on the Controller page's side panel).</summary>
    [ObservableProperty] private bool _isPresetChooserOpen;

    [RelayCommand(CanExecute = nameof(CanLoadPreset))]
    private void LoadPreset()
    {
        if (!CanLoadPreset) return;
        ToController();
        IsPresetChooserOpen = true;
    }

    /// <summary>Replaces the draft's wiring with <paramref name="preset"/>, asking first when the
    /// draft has controls; nothing is sent until Save.</summary>
    [RelayCommand]
    private void ChoosePreset(ControllerPreset? preset)
    {
        IsPresetChooserOpen = false;
        if (preset is not null && CanLoadPreset) _controller.LoadPreset(preset);
    }

    [RelayCommand]
    private void ClosePresetChooser() => IsPresetChooserOpen = false;

    private bool CanStartOver => _controller.CanStartNewDraft;

    [RelayCommand(CanExecute = nameof(CanStartOver))]
    private void StartOver()
    {
        if (!CanStartOver) return;
        ToController();
        _hardware.StartOver();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _controller.PropertyChanged -= OnSourceChanged;
        _controller.Connection.PropertyChanged -= OnSourceChanged;
        Progress.PropertyChanged -= OnProgressChanged;
        Progress.Dispose();
    }
}
