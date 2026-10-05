using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Firmware;
using Volco.Core.Runtime;
using Volco.Core.Updates;

namespace Volco.App.ViewModels;

/// <summary>The Controller page's firmware part (spec §8.6): Install, Update, Check firmware, the
/// inline firmware panel, and the hint shown when the serial port gets no Hello.</summary>
public sealed partial class ControllerViewModel
{
    private FirmwareService? _firmware;
    private CancellationTokenSource? _firmwareCts;

    private void InitFirmware(FirmwareService? firmware)
    {
        _firmware = firmware;
        if (_firmware is not null) _firmware.BusyChanged += OnFirmwareBusyChanged;
        RefreshPorts();
    }

    private void DisposeFirmware()
    {
        if (_firmware is not null) _firmware.BusyChanged -= OnFirmwareBusyChanged;
        _firmwareCts?.Cancel();
    }

    private bool IsSerialConnection => _config.Config.Connection.Type == ConnectionType.Serial;

    /// <summary>Install/Update/Check can run now.</summary>
    public bool CanUseFirmware => _firmware is { IsAvailable: true, IsBusy: false } && IsSerialConnection
                                  && !IsTesting && _service.Operation == ConfigOperation.Idle;

    /// <summary>Why the firmware buttons are disabled, or null.</summary>
    public string? FirmwareHint => _firmware is not { IsAvailable: true }
        ? "This build of Volco has no firmware."
        : !IsSerialConnection ? FirmwareService.SerialOnlyMessage : null;

    /// <summary>Update is offered: a newer bundled build, or firmware this app can't talk to.</summary>
    public bool ShowUpdateFirmware => Banner.FirmwareUpdateAvailable || Banner.Actions.HasFlag(BannerActions.Update);

    /// <summary>"Firmware 3f9c2a1b5d07 (from Volco 1.2.0)" when it is the bundled build.</summary>
    private string FirmwareVersionText(string fw)
        => _firmware?.Catalog.BundledVersion == fw ? $"Firmware {fw} (from Volco {BuildInfo.DisplayVersion})" : $"Firmware {fw}";

    // ── The panel ──

    public ObservableCollection<string> FirmwarePorts { get; } = new();
    [ObservableProperty] private string? _firmwarePort;
    [ObservableProperty] private bool _isFirmwareRunning;
    [ObservableProperty] private string? _firmwareStage;
    [ObservableProperty] private double _firmwareProgress;
    [ObservableProperty] private string? _firmwareMessage;
    [ObservableProperty] private bool _offerInstall;
    [ObservableProperty] private bool _offerTest;
    [ObservableProperty] private bool _canCancelFirmware;

    public bool HasFirmwareMessage => FirmwareMessage is not null;
    partial void OnFirmwareMessageChanged(string? value) => OnPropertyChanged(nameof(HasFirmwareMessage));

    private void RefreshPorts()
    {
        string[] names;
        try { names = System.IO.Ports.SerialPort.GetPortNames(); }
        catch (Exception) { names = Array.Empty<string>(); }
        var configured = _config.Config.Connection.Serial.ComPort;
        FirmwarePorts.Clear();
        foreach (var n in names.Append(configured).Where(n => !string.IsNullOrEmpty(n)).Distinct().Order())
            FirmwarePorts.Add(n);
        // Follows the configured port until the user picks another one here.
        if (FirmwarePort is null || !FirmwarePorts.Contains(FirmwarePort)) FirmwarePort = configured;
    }

    [RelayCommand]
    private void RefreshFirmwarePorts() => RefreshPorts();

    /// <summary>Install wipes the controller's setup, so it asks first (Volco keeps its copy).</summary>
    [ObservableProperty] private bool _confirmInstallPending;

    [RelayCommand]
    private void InstallFirmware()
    {
        if (!CanUseFirmware) return;
        FirmwareMessage = null;
        OfferInstall = false;
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
    private void CancelFirmware() => _firmwareCts?.Cancel();

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
            vm.FirmwareStage = p.Stage switch
            {
                FlashStage.Connecting => "Connecting to the board…",
                FlashStage.Erasing => "Clearing the old settings…",
                FlashStage.Writing => "Writing the firmware…",
                FlashStage.Verifying => "Checking what was written…",
                _ => "Restarting the controller…",
            };
            vm.FirmwareProgress = p.Stage == FlashStage.Writing ? p.Fraction : vm.FirmwareProgress;
            // Spec §8.6: cancellation only before the first write.
            vm.CanCancelFirmware = p.Stage == FlashStage.Connecting;
        });
    }

    private bool BeginFirmware()
    {
        if (!CanUseFirmware || string.IsNullOrEmpty(FirmwarePort)) return false;
        StopTest();
        _firmwareCts = new CancellationTokenSource();
        IsFirmwareRunning = true;
        CanCancelFirmware = true;
        FirmwareMessage = null;
        OfferInstall = false;
        OfferTest = false;
        FirmwareProgress = 0;
        FirmwareStage = "Connecting to the board…";
        return true;
    }

    private void EndFirmware(string message)
    {
        IsFirmwareRunning = false;
        CanCancelFirmware = false;
        FirmwareStage = null;
        FirmwareMessage = message;
        RefreshAll();
    }

    private async Task RunProbe()
    {
        if (!BeginFirmware()) return;
        var outcome = await _firmware!.ProbeAsync(FirmwarePort!, FirmwareProgressReporter(), _firmwareCts!.Token).ConfigureAwait(false);
        _ui.Post(() =>
        {
            if (_disposed) return;
            EndFirmware(outcome.Message);
            OfferInstall = outcome.OfferInstall;
        });
    }

    private async Task RunFlash(FlashMode mode)
    {
        ConfirmInstallPending = false;
        if (!BeginFirmware()) return;
        var outcome = await _firmware!.FlashAsync(FirmwarePort!, mode, FirmwareProgressReporter(), _firmwareCts!.Token).ConfigureAwait(false);
        _ui.Post(() =>
        {
            if (_disposed) return;
            EndFirmware(outcome.Message);
            OfferTest = outcome.Succeeded;
        });
    }

    private void OnFirmwareBusyChanged(object? sender, EventArgs e) => _ui.Post(() =>
    {
        if (!_disposed) RefreshAll();
    });

    /// <summary>The NoAnswer banner's button: probe the configured port.</summary>
    [RelayCommand]
    private void CheckWhatsConnected()
    {
        RefreshPorts();
        FirmwarePort = _config.Config.Connection.Serial.ComPort;
        CheckFirmware();
    }

    private void RefreshFirmware()
    {
        OnPropertyChanged(nameof(CanUseFirmware));
        OnPropertyChanged(nameof(FirmwareHint));
        OnPropertyChanged(nameof(ShowUpdateFirmware));
        InstallFirmwareCommand.NotifyCanExecuteChanged();
    }
}
