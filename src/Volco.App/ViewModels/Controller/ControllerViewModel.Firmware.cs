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
    public static readonly TimeSpan NoHelloHintAfter = TimeSpan.FromSeconds(5);

    private FirmwareService? _firmware;
    private TimeProvider _time = TimeProvider.System;
    private ITimer? _hintTimer;
    private long? _awaitingSince;
    private CancellationTokenSource? _firmwareCts;

    private void InitFirmware(FirmwareService? firmware, TimeProvider? time)
    {
        _firmware = firmware;
        _time = time ?? TimeProvider.System;
        if (_firmware is not null) _firmware.BusyChanged += OnFirmwareBusyChanged;
        _hintTimer = _time.CreateTimer(_ => _ui.Post(CheckNoHello), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        RefreshPorts();
    }

    private void DisposeFirmware()
    {
        _hintTimer?.Dispose();
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

    /// <summary>The no-Hello hint, e.g. "No Volco controller answered on COM3. Check what's connected?"</summary>
    [ObservableProperty] private string? _noHelloHint;
    public bool HasNoHelloHint => NoHelloHint is not null;
    partial void OnNoHelloHintChanged(string? value) => OnPropertyChanged(nameof(HasNoHelloHint));
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
        FirmwarePort ??= configured;
    }

    [RelayCommand]
    private void RefreshFirmwarePorts() => RefreshPorts();

    [RelayCommand]
    private void InstallFirmware() => _ = RunFlash(FlashMode.Install);

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
        public void Report(FlashProgress p) => vm._ui.Post(() =>
        {
            if (vm._disposed) return;
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
        NoHelloHint = null;
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

    /// <summary>Spec §8.6: the serial port has been open for 5 s with no Hello.</summary>
    private void CheckNoHello()
    {
        if (_disposed) return;
        var waiting = IsSerialConnection && _runtime.ControllerState == ControllerState.AwaitingHello && !IsFirmwareRunning;
        if (!waiting)
        {
            _awaitingSince = null;
            NoHelloHint = null;
            return;
        }
        _awaitingSince ??= _time.GetTimestamp();
        if (NoHelloHint is null && _time.GetElapsedTime(_awaitingSince.Value) >= NoHelloHintAfter)
            NoHelloHint = $"No Volco controller answered on {_config.Config.Connection.Serial.ComPort}. Check what's connected?";
    }

    [RelayCommand]
    private void CheckWhatsConnected()
    {
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
