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

    /// <summary>Install, Update and Check use the port the Connection dialog picked.</summary>
    private string FirmwarePort => _config.Config.Connection.Serial.ComPort;
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

    /// <summary>The last Check firmware found no Volco firmware ("doesn't have Volco's firmware
    /// yet"): the status's button is Install firmware, until the result goes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallQuestion), nameof(InstallConfirmLabel))]
    private bool _offerInstall;

    /// <summary>The install confirmation's question: on a board the last check found without Volco's
    /// firmware, installing replaces what is on it; otherwise it is a reinstall, which erases the
    /// stored setup.</summary>
    public string InstallQuestion => OfferInstall
        ? "Install Volco's firmware on this ESP32? Anything else on it is replaced."
        : "Reinstalling erases the setup stored on the controller. Volco keeps a copy you can apply afterwards.";

    /// <summary>The install confirmation's button: Install, or Reinstall.</summary>
    public string InstallConfirmLabel => OfferInstall ? "Install" : "Reinstall";

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
        if (!CanUseFirmware || string.IsNullOrEmpty(FirmwarePort)) return false;
        StopTest();
        FirmwareActivity = activity;
        if (!IsSaving) SetProgress(null);   // a save's result belongs to the operation before
        _firmwareCts = new CancellationTokenSource();
        IsFirmwareRunning = true;
        FirmwareMessage = null;
        OfferTest = false;
        OfferInstall = false;
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

    private async Task RunProbe()
    {
        if (!BeginFirmware(LinkActivity.CheckingFirmware)) return;
        var outcome = await _firmware!.ProbeAsync(FirmwarePort, FirmwareProgressReporter(), _firmwareCts!.Token).ConfigureAwait(false);
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
            FirmwareFinished?.Invoke(this, EventArgs.Empty);
        });
    }

    private async Task RunFlash(FlashMode mode)
    {
        ConfirmInstallPending = false;
        if (!BeginFirmware(mode == FlashMode.Install ? LinkActivity.InstallingFirmware : LinkActivity.UpdatingFirmware)) return;
        var outcome = await _firmware!.FlashAsync(FirmwarePort, mode, FirmwareProgressReporter(), _firmwareCts!.Token).ConfigureAwait(false);
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
        OnPropertyChanged(nameof(CanUseFirmware));
        InstallFirmwareCommand.NotifyCanExecuteChanged();
    }
}
