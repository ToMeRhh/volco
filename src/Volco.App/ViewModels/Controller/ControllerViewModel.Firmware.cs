using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Firmware;
using Volco.Core.Runtime;

namespace Volco.App.ViewModels;

/// <summary>The firmware part (spec §8.6; hardware-page spec §4): Reinstall (today's Install),
/// Update and Check firmware, whose progress and result the status shows.</summary>
public sealed partial class ControllerViewModel
{
    private FirmwareService? _firmware;
    private CancellationTokenSource? _firmwareCts;

    private string? _configuredPort;

    private void InitFirmware(FirmwareService? firmware)
    {
        _firmware = firmware;
        if (_firmware is not null) _firmware.BusyChanged += OnFirmwareBusyChanged;
        _config.ConnectionConfigChanged += OnConnectionSettingsSaved;
        _config.ConfigChanged += OnConnectionSettingsSaved;
        _wasSerial = IsSerialConnection;
        _configuredPort = _config.Config.Connection.Serial.ComPort;
    }

    /// <summary>The connection type or port changed (the page's Connection section, or a firmware
    /// install): a result that described the old connection goes.</summary>
    private void OnConnectionSettingsSaved(object? sender, EventArgs e) => _ui.Post(() =>
    {
        if (_disposed) return;
        var serial = IsSerialConnection;
        var port = _config.Config.Connection.Serial.ComPort;
        if (serial == _wasSerial && port == _configuredPort) return;
        _wasSerial = serial;
        _configuredPort = port;
        ClearFirmwareResult();
        RefreshAll();
    });

    private bool _wasSerial = true;

    /// <summary>The last result ("Installed…", "Test your controller now") belongs to the moment it
    /// happened: it goes when the window is hidden, the controller goes away, the connection
    /// changes or the next operation starts.</summary>
    internal void ClearFirmwareResult()
    {
        if (IsFirmwareRunning) return;
        FirmwareMessage = null;
        OfferTest = false;
        OfferInstall = false;
        OfferFreshInstall = false;
        ConfirmInstallPending = false;
    }

    private void DisposeFirmware()
    {
        _config.ConnectionConfigChanged -= OnConnectionSettingsSaved;
        _config.ConfigChanged -= OnConnectionSettingsSaved;
        if (_firmware is not null) _firmware.BusyChanged -= OnFirmwareBusyChanged;
        _firmwareCts?.Cancel();
    }

    private bool IsSerialConnection => _config.Config.Connection.Type == ConnectionType.Serial;

    /// <summary>Install/Update/Check can run now.</summary>
    public bool CanUseFirmware => _firmware is { IsAvailable: true, IsBusy: false } && IsSerialConnection
                                  && !_runtime.IsUserDisconnected && !IsTesting && _service.Operation == ConfigOperation.Idle;

    /// <summary>Install, Update and Check use the port the Connection dialog picked, else the one
    /// Automatic found; null when it couldn't tell (spec §10.2: the flow asks).</summary>
    private string? FirmwarePort => _config.Config.Connection.Serial.ComPort is { Length: > 0 } manual ? manual : _runtime.UsbPort;

    private Action<string>? _pendingFirmware;

    /// <summary>The firmware flow waits for the user to say which port the controller is on (Automatic
    /// found none to use: several serial ports, none a CH340, none answering).</summary>
    [ObservableProperty] private bool _needsFirmwarePort;

    /// <summary>The question's title: "Which port is the controller on?"</summary>
    internal string FirmwareQuestionTitle => "Which port is the controller on?";

    /// <summary>The serial ports to ask about (never Automatic), described as the Connection dialog does.</summary>
    public IReadOnlyList<PortChoice> FirmwarePortChoices { get; private set; } = Array.Empty<PortChoice>();

    /// <summary>Starts the asked-about operation on <paramref name="choice"/>'s port.</summary>
    [RelayCommand]
    private void ChooseFirmwarePort(PortChoice? choice)
    {
        var run = _pendingFirmware;
        if (run is null || choice?.Port is not { } port) return;
        if (!CanUseFirmware) { CancelFirmwarePort(); return; }
        _pendingFirmware = null;
        run(port);                    // the run's start takes the dialog over: no close and reopen
        NeedsFirmwarePort = false;
    }

    /// <summary>The user gave up on the question: nothing runs.</summary>
    internal void CancelFirmwarePort()
    {
        _pendingFirmware = null;
        NeedsFirmwarePort = false;
    }

    /// <summary>Asks which port when <see cref="FirmwarePort"/> is unknown; with no serial port at
    /// all it ends at once with a result saying so.</summary>
    private void AskFirmwarePort(LinkActivity activity, Action<string> run)
    {
        if (!CanUseFirmware || NeedsFirmwarePort) return;
        var ports = _runtime.ListSerialPorts();
        if (ports.Count == 0)
        {
            // Under Automatic this is the ordinary "not plugged in": say so in the progress dialog.
            if (!BeginFirmware(activity)) return;
            EndFirmware("No USB port found. Plug the controller in.", LinkTone.Warning);
            FirmwareFinished?.Invoke(this, EventArgs.Empty);
            return;
        }
        FirmwarePortChoices = ports.Select(p => new PortChoice(p.Name, ConnectionViewModel.PortLabel(p))).ToList();
        _pendingFirmware = run;
        NeedsFirmwarePort = true;
    }
    [ObservableProperty] private bool _isFirmwareRunning;
    [ObservableProperty] private double _firmwareProgress;
    [ObservableProperty] private string? _firmwareMessage;

    /// <summary>The running write's stage, as the flasher last reported it; null before the first
    /// report and when nothing runs.</summary>
    [ObservableProperty] private FlashStage? _firmwareStep;

    /// <summary>A firmware operation ended and its result, tone and offers are set (the progress
    /// dialog shows them). UI thread.</summary>
    internal event EventHandler? FirmwareFinished;

    /// <summary>The card offers "Test your controller now": after an Update or a first save.</summary>
    [ObservableProperty] private bool _offerTest;

    /// <summary>The last Check firmware offers an install (no Volco firmware, a different build, or
    /// Volco's firmware that didn't answer): the status's button installs, until the result goes.</summary>
    [ObservableProperty] private bool _offerInstall;

    /// <summary>The offered install is a first one: the board never answered as a Volco controller
    /// this run ("doesn't have Volco's firmware yet"). The status's button is Install firmware and
    /// the confirmation isn't a warning; otherwise it is Reinstall firmware…, which erases the
    /// stored setup.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallQuestion), nameof(InstallConfirmLabel))]
    private bool _offerFreshInstall;

    /// <summary>The install confirmation's question: on a board without Volco's firmware, installing
    /// replaces what is on it; otherwise it is a reinstall, which erases the stored setup.</summary>
    public string InstallQuestion => OfferFreshInstall
        ? "Install Volco's firmware on this ESP32? Anything else on it is replaced."
        : "Reinstalling erases the setup stored on the controller. Volco keeps a copy you can apply afterwards.";

    /// <summary>The install confirmation's button: Install, or Reinstall.</summary>
    public string InstallConfirmLabel => OfferFreshInstall ? "Install" : "Reinstall";

    /// <summary>The result's tone: Volco's firmware found or written is good, other firmware a
    /// warning, a failure a danger.</summary>
    [ObservableProperty] private LinkTone _firmwareMessageTone;

    /// <summary>Install wipes the controller's setup, so it asks first on the card (Volco keeps its
    /// copy). Nothing changes until it is confirmed: Cancel leaves the page as it was.</summary>
    [ObservableProperty] private bool _confirmInstallPending;

    [RelayCommand]
    private void InstallFirmware()
    {
        if (!CanUseFirmware) return;
        ConfirmInstallPending = true;
    }

    [RelayCommand]
    private void ConfirmInstall()
    {
        ConfirmInstallPending = false;
        _ = RunFlash(FlashMode.Install);
    }

    [RelayCommand]
    private void CancelInstall() => ConfirmInstallPending = false;

    [RelayCommand]
    private void UpdateFirmware() => _ = RunFlash(FlashMode.Update);

    [RelayCommand]
    private void CheckFirmware() => _ = RunProbe();

    [RelayCommand]
    private void TestNow()
    {
        OfferTest = false;
        StartTest();
    }

    /// <summary>The progress dialog was closed without taking the test it offered: the offer goes.</summary>
    internal void DeclineTest() => OfferTest = false;

    /// <summary>The progress dialog's Try again: the operation that just failed, once more (an
    /// Install was confirmed already).</summary>
    internal void RetryFirmware(LinkActivity activity)
    {
        switch (activity)
        {
            case LinkActivity.InstallingFirmware: _ = RunFlash(FlashMode.Install); break;
            case LinkActivity.UpdatingFirmware: _ = RunFlash(FlashMode.Update); break;
            case LinkActivity.CheckingFirmware: _ = RunProbe(); break;
        }
    }

    private IProgress<FlashProgress> FirmwareProgressReporter() => new UiProgress(this);

    private sealed class UiProgress(ControllerViewModel vm) : IProgress<FlashProgress>
    {
        private FlashStage _latest = FlashStage.Connecting;

        public void Report(FlashProgress p) => vm._ui.Post(() =>
        {
            if (vm._disposed || !vm.IsFirmwareRunning || p.Stage < _latest) return;   // late or after the end
            _latest = p.Stage;
            vm.FirmwareStep = p.Stage;
            vm.FirmwareProgress = p.Stage == FlashStage.Writing ? p.Fraction : vm.FirmwareProgress;
        });
    }

    /// <summary>Which firmware operation runs (checking, installing or updating); None when none does.</summary>
    internal LinkActivity FirmwareActivity { get; private set; }

    private bool BeginFirmware(LinkActivity activity)
    {
        if (!CanUseFirmware) return false;
        StopTest();
        FirmwareActivity = activity;
        if (!IsSaving) SetProgress(null);   // a save's result belongs to the operation before
        _firmwareCts = new CancellationTokenSource();
        IsFirmwareRunning = true;
        FirmwareMessage = null;
        OfferTest = false;
        OfferInstall = false;
        OfferFreshInstall = false;
        FirmwareProgress = 0;
        FirmwareStep = null;
        return true;
    }

    private void EndFirmware(string message, LinkTone tone)
    {
        IsFirmwareRunning = false;
        FirmwareActivity = LinkActivity.None;
        FirmwareStep = null;
        FirmwareMessageTone = tone;
        FirmwareMessage = message;
        RefreshAll();
    }

    private async Task RunProbe(string? port = null)
    {
        port ??= FirmwarePort;
        if (port is null) { AskFirmwarePort(LinkActivity.CheckingFirmware, p => _ = RunProbe(p)); return; }
        if (!BeginFirmware(LinkActivity.CheckingFirmware)) return;
        var outcome = await _firmware!.ProbeAsync(port, FirmwareProgressReporter(), _firmwareCts!.Token).ConfigureAwait(false);
        _ui.Post(() =>
        {
            if (_disposed) return;
            EndFirmware(outcome.Message, outcome.Result?.Kind switch
            {
                ProbeKind.VolcoMatches => LinkTone.Good,
                ProbeKind.DifferentFirmware => LinkTone.Warning,
                _ => LinkTone.Danger,
            });
            OfferInstall = outcome.OfferInstall;
            OfferFreshInstall = outcome.OfferInstall && outcome.OfferFreshInstall;
            FirmwareFinished?.Invoke(this, EventArgs.Empty);
        });
    }

    private async Task RunFlash(FlashMode mode, string? port = null)
    {
        ConfirmInstallPending = false;
        port ??= FirmwarePort;
        if (port is null) { AskFirmwarePort(mode == FlashMode.Install ? LinkActivity.InstallingFirmware : LinkActivity.UpdatingFirmware, p => _ = RunFlash(mode, p)); return; }
        if (!BeginFirmware(mode == FlashMode.Install ? LinkActivity.InstallingFirmware : LinkActivity.UpdatingFirmware)) return;
        var outcome = await _firmware!.FlashAsync(port, mode, FirmwareProgressReporter(), _firmwareCts!.Token).ConfigureAwait(false);
        _ui.Post(() =>
        {
            if (_disposed) return;
            EndFirmware(outcome.Message, outcome.Succeeded ? LinkTone.Good : LinkTone.Danger);
            OfferTest = outcome.Succeeded && mode == FlashMode.Update;
            FirmwareFinished?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnFirmwareBusyChanged(object? sender, EventArgs e) => _ui.Post(() =>
    {
        if (!_disposed) RefreshAll();
    });

    private void RefreshFirmware()
    {
        // The question is only valid while the page could still act on it (a save, a test or a
        // connection change took the port away).
        if (NeedsFirmwarePort && !CanUseFirmware) CancelFirmwarePort();
        OnPropertyChanged(nameof(CanUseFirmware));
        InstallFirmwareCommand.NotifyCanExecuteChanged();
    }
}
