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
    }

    /// <summary>The page's controller logic: the Install confirmation binds to it.</summary>
    public ControllerViewModel Controller => _controller;

    private ConnectionViewModel Connection => _controller.Connection;

    public string Name => DeviceCard.Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionLabel), nameof(HasAction), nameof(HasProgress), nameof(ShortText), nameof(FullText))]
    private CardState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShortText), nameof(ShowsBanner))]
    private StatusLevel _level;

    /// <summary>The sidebar line's words: "Connected" or "Connecting…" when all is fine, else the
    /// state's status line.</summary>
    public string ShortText => Level != StatusLevel.Fine ? State.Status
                             : State.Tone == LinkTone.Good ? "Connected" : "Connecting…";

    /// <summary>The status and its detail on one line: the banner's text and the sidebar line's tooltip.</summary>
    public string FullText => State.Detail is { } detail ? $"{State.Status} · {detail}" : State.Status;

    /// <summary>The banner shows while the status needs the user. A running operation shows there
    /// too until the progress dialog takes it over.</summary>
    public bool ShowsBanner => Level != StatusLevel.Fine;

    /// <summary>Raised before an action that belongs to the drawing acts (Fix it, Test your
    /// controller now, Load a preset…, Start over…, the not-set-up states' chooser): the window
    /// shows the Controller page first (controller-page spec §5).</summary>
    public event EventHandler? RequestController;

    private void ToController() => RequestController?.Invoke(this, EventArgs.Empty);

    public string ActionLabel => DeviceCard.Label(State.Action);
    public bool HasAction => State.Action != CardAction.None;
    public bool HasProgress => State.Progress is not null;

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
                              c.SavePhaseText, c.Failure, c.OfferTest, c.OfferInstall);
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        var inputs = Inputs();
        State = DeviceCard.From(inputs);
        Level = DeviceCard.LevelOf(inputs, State);
        ActionCommand.NotifyCanExecuteChanged();
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
        CardAction.Install => _controller.CanUseFirmware && !_controller.Busy,
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
            case CardAction.Install: _controller.InstallFirmwareCommand.Execute(null); break;   // asks first
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
    }
}
