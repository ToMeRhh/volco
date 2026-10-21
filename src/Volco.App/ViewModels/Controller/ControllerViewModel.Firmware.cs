using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Firmware;
using Volco.Core.Runtime;

namespace Volco.App.ViewModels;

/// <summary>The firmware part (spec §8.6; hardware-page spec §4): Reinstall (today's Install),
/// Update and Check firmware, whose progress and result the device card shows.</summary>
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

    /// <summary>The last result ("Installed…", "Test your controller now") belongs to the moment it happened: it goes when the page is left, the controller goes away or
    /// the connection changes.</summary>
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

    /// <summary>The card offers "Test your controller now": after an Update or a first save.</summary>
    [ObservableProperty] private bool _offerTest;

    /// <summary>The last Check firmware asks to reinstall ("doesn't have this version…"): the card's
    /// button is Reinstall firmware…, until the result goes.</summary>
    [ObservableProperty] private bool _offerInstall;

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

    private IProgress<FlashProgress> FirmwareProgressReporter() => new UiProgress(this);

    private sealed class UiProgress(ControllerViewModel vm) : IProgress<FlashProgress>
    {
        private FlashStage _latest = FlashStage.Connecting;

        public void Report(FlashProgress p) => vm._ui.Post(() =>
        {
            if (vm._disposed || !vm.IsFirmwareRunning || p.Stage < _latest) return;   // late or after the end
            _latest = p.Stage;
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
        return true;
    }

    private void EndFirmware(string message, LinkTone tone)
    {
        IsFirmwareRunning = false;
        FirmwareActivity = LinkActivity.None;
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
            // After an Install the controller has no setup yet: the setup chooser comes next.
            EndFirmware(outcome.Message, outcome.Succeeded ? LinkTone.Good : LinkTone.Danger);
            OfferTest = outcome.Succeeded && mode == FlashMode.Update;
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
