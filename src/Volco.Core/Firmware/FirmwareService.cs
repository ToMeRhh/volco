using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Diagnostics;
using Volco.Core.Runtime;

namespace Volco.Core.Firmware;

/// <param name="OfferInstall">The page offers Install next to the message.</param>
/// <param name="OfferFreshInstall">The offer is a first install: the board has never answered as a
/// Volco controller this run, so nothing of Volco's is on it to erase. Otherwise it is a reinstall,
/// which erases the setup stored on the controller.</param>
public sealed record ProbeOutcome(ProbeResult? Result, string Message, bool OfferInstall, bool OfferFreshInstall = false);

public sealed record FlashOutcome(bool Succeeded, string Message);

/// <summary>
/// Probe, Install and Update (spec §8.6), one at a time. Each holds a connection suspension so
/// nothing reopens the port, writes a port chosen in the dialog to the config while still
/// suspended, resumes, and after a flash waits for the controller's Hello with the bundled build.
/// </summary>
public sealed class FirmwareService
{
    public static readonly TimeSpan HelloWait = TimeSpan.FromSeconds(10);
    public const string SerialOnlyMessage = "Plug the controller in with USB and choose \"USB cable\" under Connection.";
    public const string DisconnectedMessage = "Volco is disconnected. Press Connect first.";
    public const string NoVolcoFirmwareMessage = "This ESP32 doesn't have Volco's firmware yet.";
    public const string DifferentBuildMessage = "This ESP32 runs a different build of Volco's firmware.";
    public const string SilentMessage = "Volco's firmware is installed but didn't answer. Reinstall it?";

    private readonly VolcoRuntime _runtime;
    private readonly IFirmwareFlasher? _flasher;
    private readonly FirmwareCatalog _catalog;
    private readonly TimeProvider _time;
    private readonly Func<Action, Task> _configThread;
    private int _busy;
    private int _seenHello;

    /// <param name="configThread">Runs a config edit on the thread that owns the config (the UI
    /// thread in the app); by default the edit runs inline.</param>
    public FirmwareService(VolcoRuntime runtime, IFirmwareFlasher? flasher, FirmwareCatalog catalog, TimeProvider? time = null,
                           Func<Action, Task>? configThread = null)
    {
        _runtime = runtime;
        _flasher = flasher;
        _catalog = catalog;
        _time = time ?? TimeProvider.System;
        _configThread = configThread ?? (a => { a(); return Task.CompletedTask; });
        if (_runtime.ControllerConfig.LastHello is not null) _seenHello = 1;
        _runtime.HelloReceived += (_, _) => Volatile.Write(ref _seenHello, 1);
    }

    /// <summary>A controller has said Hello this run: a board the probe finds without the bundled
    /// build runs another build of Volco's firmware, not none.</summary>
    public bool SeenVolcoHello => Volatile.Read(ref _seenHello) != 0;

    /// <summary>This build can flash: it has a flasher and bundled firmware.</summary>
    public bool IsAvailable => _flasher is not null && _catalog.HasFirmware;
    public FirmwareCatalog Catalog => _catalog;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    /// <summary>A probe or flash started or ended. Any thread.</summary>
    public event EventHandler? BusyChanged;

    private bool IsSerial => _runtime.Config.Config.Connection.Type == ConnectionType.Serial;

    public async Task<ProbeOutcome> ProbeAsync(string port, IProgress<FlashProgress> progress, CancellationToken ct = default)
    {
        if (!IsAvailable) return new(null, "This build of Volco has no firmware.", false);
        if (!IsSerial) return new(null, SerialOnlyMessage, false);
        if (_runtime.IsUserDisconnected) return new(null, DisconnectedMessage, false);
        if (!TryEnter()) return new(null, "Another firmware operation is running.", false);
        using var expected = _runtime.ExpectInterruption(LinkActivity.CheckingFirmware);
        try
        {
            ProbeResult result;
            // The probe resets the board: when it holds the bundled build, its Hello after the
            // resume tells "installed and answering" apart from "installed but silent".
            var version = _catalog.BundledVersion;
            var hello = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnHello(object? s, HelloMessage h)
            {
                if (h.FirmwareVersion == version) hello.TrySetResult();
            }
            using (_runtime.SuspendConnection())
            {
                try { result = await _flasher!.ProbeAsync(port, _catalog.Esp32!, progress, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return new(null, "Cancelled.", false); }
                catch (Exception e)
                {
                    AppLog.Error($"Firmware probe failed: {e}");
                    return new(null, $"Couldn't check the board on {port}: {e.Message}", false);
                }
                AppLog.Info($"Firmware probe on {port}: {result}");
                if (result.Kind is ProbeKind.VolcoMatches or ProbeKind.DifferentFirmware) await SavePortAsync(port).ConfigureAwait(false);
                if (result.Kind == ProbeKind.VolcoMatches) _runtime.HelloReceived += OnHello;   // before the resume
            }
            if (result.Kind == ProbeKind.VolcoMatches)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(HelloWait, _time);
                    await hello.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                    return new(result, $"This controller runs Volco's firmware {version}.", false);
                }
                catch (OperationCanceledException)
                {
                    return new(result, SilentMessage, true);
                }
                finally { _runtime.HelloReceived -= OnHello; }
            }
            return result.Kind switch
            {
                ProbeKind.NoDevice => new(result, result.Message ?? $"No ESP32 found on {port}.", false),
                ProbeKind.UnsupportedChip => new(result, new UnsupportedChipException(result.ChipName ?? "unknown chip").Message, false),
                // Another build of Volco's (a Hello came this run) loses its stored setup to a
                // reinstall; a board that never answered as Volco has nothing of Volco's on it.
                _ when SeenVolcoHello => new(result, DifferentBuildMessage, true),
                _ => new(result, NoVolcoFirmwareMessage, true, OfferFreshInstall: true),
            };
        }
        finally { Exit(); }
    }

    public async Task<FlashOutcome> FlashAsync(string port, FlashMode mode, IProgress<FlashProgress> progress, CancellationToken ct = default)
    {
        if (!IsAvailable) return new(false, "This build of Volco has no firmware.");
        if (!IsSerial) return new(false, SerialOnlyMessage);
        // After a manual Disconnect the resume wouldn't reconnect, so the Hello could never come.
        if (_runtime.IsUserDisconnected) return new(false, DisconnectedMessage);
        if (!TryEnter()) return new(false, "Another firmware operation is running.");
        using var expected = _runtime.ExpectInterruption(mode == FlashMode.Install ? LinkActivity.InstallingFirmware : LinkActivity.UpdatingFirmware);
        var verb = mode == FlashMode.Install ? "Install" : "Update";
        try
        {
            var image = _catalog.Esp32!;
            var version = image.Manifest.Version;
            // Subscribed before the port reopens, so the post-boot Hello can't be missed.
            var hello = new TaskCompletionSource<HelloMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnHello(object? s, HelloMessage h)
            {
                if (h.FirmwareVersion == version) hello.TrySetResult(h);
            }

            using (_runtime.SuspendConnection())
            {
                var started = _time.GetTimestamp();
                try { await _flasher!.FlashAsync(port, image, mode, progress, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return new(false, "Cancelled. Nothing was written."); }
                catch (NoDeviceException e) { return new(false, e.Message); }
                catch (UnsupportedChipException e) { return new(false, e.Message); }
                catch (Exception e)
                {
                    AppLog.Error($"Firmware {verb.ToLowerInvariant()} failed: {e}");
                    return new(false, e is FirmwareFlashException { Written: false }
                        ? $"{verb} failed: {e.Message}. Nothing was written. Try again."
                        : $"{verb} failed: {e.Message}. The controller won't work until the {verb.ToLowerInvariant()} succeeds. Try again.");
                }
                AppLog.Info($"Firmware {verb.ToLowerInvariant()} of {version} on {port} took {_time.GetElapsedTime(started).TotalSeconds:F1} s");
                await SavePortAsync(port).ConfigureAwait(false);   // still suspended: the change can't restart the connection mid-flash
                _runtime.HelloReceived += OnHello;
            }

            if (_runtime.IsUserDisconnected)
            {
                _runtime.HelloReceived -= OnHello;
                var done0 = mode == FlashMode.Install ? "Installed" : "Updated";
                return new(true, $"{done0} firmware {version}. Volco is disconnected: Connect to see the controller.");
            }
            try
            {
                using var timeout = new CancellationTokenSource(HelloWait, _time);
                await hello.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                var done = mode == FlashMode.Install ? "Installed" : "Updated";
                return new(true, $"{done}. The controller is running firmware {version}.");
            }
            catch (OperationCanceledException)
            {
                return new(false, "The firmware was installed but the controller didn't answer. Check the connection settings.");
            }
            finally { _runtime.HelloReceived -= OnHello; }
        }
        finally { Exit(); }
    }

    /// <summary>The dialog's port becomes the configured one (spec §8.6 step 3), saved while the
    /// connection is suspended; the resume then opens it.</summary>
    private async Task SavePortAsync(string port)
    {
        var serial = _runtime.Config.Config.Connection.Serial;
        if (serial.ComPort == port) return;
        try
        {
            await _configThread(() =>
            {
                serial.ComPort = port;
                _runtime.Config.Save();
            }).ConfigureAwait(false);
        }
        catch (Exception e) { AppLog.Error($"Couldn't save the port {port}: {e.Message}"); }
    }

    private bool TryEnter()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;
        BusyChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Exit()
    {
        Volatile.Write(ref _busy, 0);
        BusyChanged?.Invoke(this, EventArgs.Empty);
    }
}
