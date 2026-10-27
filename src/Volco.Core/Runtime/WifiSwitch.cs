using System.Threading.Channels;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Volco.Core.Diagnostics;

namespace Volco.Core.Runtime;

/// <summary>The Connection dialog's Wi-Fi fields (spec §10.3).</summary>
/// <param name="PcIp">This PC's IPv4 address, a.b.c.d = a&lt;&lt;24|b&lt;&lt;16|c&lt;&lt;8|d (the proto's pc_ip).</param>
/// <param name="ListenPort">The port Volco listens on (<c>connection.udp.listenPort</c>), which is
/// also the controller's PC port.</param>
/// <param name="ControllerPort">The controller's own port (local_port).</param>
public sealed record WifiSettings(string Ssid, string Password, uint PcIp, int ListenPort, int ControllerPort);

public enum WifiSwitchOutcome
{
    /// <summary>The controller answered over Wi-Fi on the new settings; Volco is on Wi-Fi.</summary>
    OnWifi,
    /// <summary>Turning Wi-Fi off: the controller answered over USB and saved its config with Wi-Fi off.</summary>
    BackOnUsb,
    /// <summary>The controller answered over Wi-Fi on settings other than the new ones: it went
    /// back to its previous Wi-Fi settings. Volco stays on Wi-Fi.</summary>
    FellBack,
    /// <summary>The controller didn't answer over Wi-Fi. After Save and switch Volco is back on
    /// USB; after a save over Wi-Fi it stays on Wi-Fi, on its old listen port.</summary>
    NoAnswer,
    /// <summary>The save didn't go through (or the settings are invalid); Volco's transport is unchanged.</summary>
    SaveFailed,
    /// <summary>The connected controller's setup isn't the one Volco has saved (its Hello's CRC
    /// differs from the mirror's): saving the mirror with new Wi-Fi settings would replace it
    /// (spec §8.4). Nothing was sent.</summary>
    DifferentSetup,
    /// <summary>Save and switch needs a controller connected over USB; a save over Wi-Fi needs one
    /// connected over Wi-Fi. Nothing was sent.</summary>
    NeedsUsb,
    /// <summary>Cancelled, disposed, another Wi-Fi operation is running, or (turning Wi-Fi off)
    /// Volco isn't on Wi-Fi or the transport was changed meanwhile. The transport is left as it
    /// was at that moment.</summary>
    Cancelled,
}

public enum WifiSwitchStep
{
    /// <summary>Sending the new config.</summary>
    Saving,
    /// <summary>The controller accepted it and restarts ("Saved to the controller · restarting").</summary>
    Restarting,
    /// <summary>Waiting for a Hello over Wi-Fi ("Waiting for the controller over Wi-Fi… 9 s").</summary>
    WaitingForWifi,
}

/// <summary>
/// The Connection dialog's Wi-Fi operations (spec §10.2, §10.3): Save and switch (send the Wi-Fi
/// settings over USB, then move Volco to Wi-Fi and wait for the controller there), Save over
/// Wi-Fi (Volco moves its listen port with the controller, and back if the controller falls back
/// to its old settings), and turning Wi-Fi off (switch from Wi-Fi to USB, then, on the first USB
/// Hello of this run, save the config with Wi-Fi off). One at a time. Owned for the whole run, not
/// by the dialog, so closing the dialog never drops a pending turn-off; <see cref="Dispose"/>
/// cancels whatever runs and leaves the transport as it is.
/// <para>
/// Every save is the mirror with only its Wi-Fi section replaced, so it is made only while the
/// connected controller runs the mirror's setup (its last Hello's CRC); otherwise the outcome is
/// <see cref="WifiSwitchOutcome.DifferentSetup"/>.
/// </para>
/// <para>
/// Threading: the public methods may be awaited from any thread; config edits run on the config
/// thread. A wait after a transport edit takes only Hellos from a transport built after that edit
/// (<see cref="VolcoRuntime.TransportHelloReceived"/>).
/// </para>
/// </summary>
public sealed class WifiSwitch : IDisposable
{
    /// <summary>How long the controller tries new Wi-Fi settings after it boots before it goes back
    /// to the previous ones (the firmware's fallback window).</summary>
    public static readonly TimeSpan FallbackWindow = TimeSpan.FromSeconds(30);

    /// <summary>How much longer than the fallback window Volco waits for the controller.</summary>
    public static readonly TimeSpan AnswerWait = TimeSpan.FromSeconds(15);

    /// <summary>How long a save waits for a running config read (the quiet read after a Hello) to end.</summary>
    public static readonly TimeSpan BusyWait = TimeSpan.FromSeconds(10);

    private readonly VolcoRuntime _runtime;
    private readonly Func<Action, Task> _configThread;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _disposed = new();
    private int _busy;
    private CancellationTokenSource? _turnOff;   // the pending turn-off's own cancellation; null when none

    /// <param name="configThread">Runs a config edit on the thread that owns the config (the UI
    /// thread in the app).</param>
    public WifiSwitch(VolcoRuntime runtime, Func<Action, Task> configThread)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _configThread = configThread ?? throw new ArgumentNullException(nameof(configThread));
        _time = runtime.Time;
    }

    /// <summary>A Wi-Fi operation is running (a turn-off included, while it waits for USB).</summary>
    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    /// <summary>Turning Wi-Fi off waits for the controller's first Hello over USB.</summary>
    public bool TurnOffPending => Volatile.Read(ref _turnOff) is not null;

    /// <summary><see cref="IsBusy"/> or <see cref="TurnOffPending"/> changed. Any thread.</summary>
    public event EventHandler? BusyChanged;

    /// <summary>Drops a pending turn-off (its <see cref="TurnOffAsync"/> returns
    /// <see cref="WifiSwitchOutcome.Cancelled"/>); a save it already started still ends on its own.</summary>
    public void CancelTurnOff() => Cancel(Volatile.Read(ref _turnOff));

    /// <summary>The Wi-Fi fields' starting values: the controller's saved Wi-Fi settings, with
    /// Volco's <paramref name="listenPort"/>. Null when the mirror has Wi-Fi off, has none, or
    /// doesn't decode.</summary>
    public static WifiSettings? FromMirror(byte[]? mirror, int listenPort)
    {
        if (!Decodes(mirror)) return null;
        var config = ControllerConfigCodec.Decode(mirror!);
        if (config.Udp is not { Enabled: true } udp) return null;
        return new WifiSettings(udp.Ssid, udp.Password, udp.PcIp, listenPort, (int)udp.LocalPort);
    }

    /// <summary>Save and switch (Volco on USB, spec §10.3): saves <paramref name="settings"/> to the
    /// controller over USB, switches Volco to Wi-Fi on <see cref="WifiSettings.ListenPort"/> and
    /// waits <see cref="FallbackWindow"/> + <see cref="AnswerWait"/> for a Hello. With none, Volco
    /// goes back to USB and its old listen port.</summary>
    public async Task<WifiSwitchOutcome> SaveAndSwitchAsync(WifiSettings settings, IProgress<(WifiSwitchStep Step, TimeSpan? Left)>? progress = null,
                                                            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!TryEnter()) return WifiSwitchOutcome.Cancelled;
        using var op = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
        var token = op.Token;
        try
        {
            if (!ConnectedOver(ConnectionType.Serial)) return WifiSwitchOutcome.NeedsUsb;

            progress?.Report((WifiSwitchStep.Saving, null));
            var begun = await BeginSaveAsync(mirror => Blob(mirror, WifiOn(settings)), token).ConfigureAwait(false);
            if (begun.Run is not { } run) return begun.Failure;
            var crc = ControllerConfigCodec.Crc32(begun.Blob!);
            await Task.WhenAny(run.Save, run.Accepted).WaitAsync(token).ConfigureAwait(false);
            if (run.Accepted.IsCompleted) progress?.Report((WifiSwitchStep.Restarting, null));   // both may be done
            var saved = await run.Save.WaitAsync(token).ConfigureAwait(false);
            if (saved.Status != SaveStatus.Confirmed) return Failed(saved);

            using var hellos = new HelloWatch(_runtime, ConnectionType.Udp, _time);   // before the switch: its Hello can't be missed
            var oldPort = _runtime.Config.Config.Connection.Udp.ListenPort;
            AppLog.Info($"Wi-Fi switch: saved, switching to Wi-Fi on port {settings.ListenPort}");
            hellos.After(await OnConfigThreadAsync(c =>
            {
                c.Type = ConnectionType.Udp;
                c.Udp.ListenPort = settings.ListenPort;
            }, token).ConfigureAwait(false));

            var end = _time.GetUtcNow() + FallbackWindow + AnswerWait;
            using (_runtime.ExpectInterruption(LinkActivity.Connecting))
            using (Countdown(progress, end))
            {
                if (await hellos.NextAsync(end, token).ConfigureAwait(false) is { } hello)
                {
                    var outcome = IsNew(hello, crc) ? WifiSwitchOutcome.OnWifi : WifiSwitchOutcome.FellBack;
                    AppLog.Info($"Wi-Fi switch: {outcome} (config crc {hello.ConfigCrc:X8})");
                    return outcome;
                }
            }

            AppLog.Info("Wi-Fi switch: no answer over Wi-Fi, back to USB");
            await OnConfigThreadAsync(c =>
            {
                c.Type = ConnectionType.Serial;
                c.Udp.ListenPort = oldPort;
            }, token).ConfigureAwait(false);
            return WifiSwitchOutcome.NoAnswer;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            AppLog.Info("Wi-Fi switch: cancelled");
            return WifiSwitchOutcome.Cancelled;
        }
        finally { Exit(); }
    }

    /// <summary>Save (Volco already on Wi-Fi, spec §10.3): saves <paramref name="settings"/> over
    /// Wi-Fi and, once the controller accepts them, moves Volco's listen port with it (the restarted
    /// controller sends its Hello there, so the save's own confirmation can't come on the old one).
    /// Waits <see cref="FallbackWindow"/> from the controller's OK for it on the new settings; then
    /// returns to the old listen port and waits <see cref="AnswerWait"/> for it there (it fell
    /// back). Volco stays on Wi-Fi.</summary>
    public async Task<WifiSwitchOutcome> SaveOverWifiAsync(WifiSettings settings, IProgress<(WifiSwitchStep Step, TimeSpan? Left)>? progress = null,
                                                           CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!TryEnter()) return WifiSwitchOutcome.Cancelled;
        using var op = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
        var token = op.Token;
        try
        {
            if (!ConnectedOver(ConnectionType.Udp)) return WifiSwitchOutcome.NeedsUsb;

            progress?.Report((WifiSwitchStep.Saving, null));
            var begun = await BeginSaveAsync(mirror => Blob(mirror, WifiOn(settings)), token).ConfigureAwait(false);
            if (begun.Run is not { } run) return begun.Failure;
            var blob = begun.Blob!;
            var crc = ControllerConfigCodec.Crc32(blob);
            await Task.WhenAny(run.Save, run.Accepted).WaitAsync(token).ConfigureAwait(false);
            if (!run.Accepted.IsCompleted)   // WhenAny may pick the save when both are done
            {
                // Ended before an OK: an implied OK (a confirming Hello) or a failure.
                var early = await run.Save.ConfigureAwait(false);
                return early.Status == SaveStatus.Confirmed ? WifiSwitchOutcome.OnWifi : Failed(early);
            }

            // The controller accepted the write and restarts into the new settings. With a new
            // listen port Volco moves there now; the save itself then ends as ConnectionLost (the
            // transport is replaced), and the Hello below is the confirmation.
            progress?.Report((WifiSwitchStep.Restarting, null));
            var newSettingsEnd = _time.GetUtcNow() + FallbackWindow;
            var end = newSettingsEnd + AnswerWait;
            using var hellos = new HelloWatch(_runtime, ConnectionType.Udp, _time);
            var oldPort = _runtime.Config.Config.Connection.Udp.ListenPort;
            var moved = settings.ListenPort != oldPort;
            if (moved)
            {
                AppLog.Info($"Wi-Fi save: accepted, listening on port {settings.ListenPort}");
                hellos.After(await OnConfigThreadAsync(c => c.Udp.ListenPort = settings.ListenPort, token).ConfigureAwait(false));
            }

            using (_runtime.ExpectInterruption(LinkActivity.Connecting))
            using (Countdown(progress, end))
            {
                await run.Save.WaitAsync(token).ConfigureAwait(false);   // its confirming Hello, if any, is in hellos too
                if (await hellos.NextAsync(newSettingsEnd, token).ConfigureAwait(false) is { } hello)
                {
                    if (!IsNew(hello, crc))
                    {
                        AppLog.Info($"Wi-Fi save: fell back (config crc {hello.ConfigCrc:X8})");
                        return WifiSwitchOutcome.FellBack;
                    }
                    // A replaced transport cut the save short: this Hello is its confirmation.
                    _runtime.ControllerConfig.AdoptConfirmed(blob);
                    AppLog.Info("Wi-Fi save: on the new settings");
                    return WifiSwitchOutcome.OnWifi;
                }

                // Not on the new settings within the window: the controller goes back to its old ones.
                if (moved)
                {
                    AppLog.Info($"Wi-Fi save: no answer on the new settings, back to port {oldPort}");
                    hellos.After(await OnConfigThreadAsync(c => c.Udp.ListenPort = oldPort, token).ConfigureAwait(false));
                }
                if (await hellos.NextAsync(end, token).ConfigureAwait(false) is { } late)
                {
                    if (!moved && IsNew(late, crc))
                    {
                        _runtime.ControllerConfig.AdoptConfirmed(blob);
                        AppLog.Info("Wi-Fi save: on the new settings");
                        return WifiSwitchOutcome.OnWifi;
                    }
                    AppLog.Info($"Wi-Fi save: fell back (config crc {late.ConfigCrc:X8})");
                    return WifiSwitchOutcome.FellBack;
                }
            }
            AppLog.Info("Wi-Fi save: no answer over Wi-Fi");
            return WifiSwitchOutcome.NoAnswer;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            AppLog.Info("Wi-Fi save: cancelled");
            return WifiSwitchOutcome.Cancelled;
        }
        finally { Exit(); }
    }

    /// <summary>Switch to USB and turn Wi-Fi off (spec §10.2), from Wi-Fi only: switches Volco to
    /// USB at once, waits for the controller's first Hello over USB (no timeout), then saves the
    /// controller's config with Wi-Fi off. Volco stays on USB whatever the outcome. Not on Wi-Fi →
    /// <see cref="WifiSwitchOutcome.Cancelled"/> and nothing changes. The wait ends with
    /// Cancelled on <paramref name="ct"/>, <see cref="CancelTurnOff"/>, <see cref="Dispose"/>, or
    /// when the transport is changed to anything but USB meanwhile (the user picks the simulator).</summary>
    public async Task<WifiSwitchOutcome> TurnOffAsync(CancellationToken ct = default)
    {
        if (_runtime.Config.Config.Connection.Type != ConnectionType.Udp) return WifiSwitchOutcome.Cancelled;
        if (!TryEnter()) return WifiSwitchOutcome.Cancelled;
        using var own = new CancellationTokenSource();
        using var op = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token, own.Token);
        var token = op.Token;
        void OnConnectionChanged(object? s, EventArgs e)
        {
            if (_runtime.Config.Config.Connection.Type != ConnectionType.Serial)
            {
                AppLog.Info("Wi-Fi off: the transport changed; Wi-Fi stays as it is");
                Cancel(own);
            }
        }
        _runtime.Config.ConnectionConfigChanged += OnConnectionChanged;
        SetTurnOff(own);
        try
        {
            using var hellos = new HelloWatch(_runtime, ConnectionType.Serial, _time);
            AppLog.Info("Wi-Fi off: switching to USB; Wi-Fi goes off at the first USB Hello");
            hellos.After(await OnConfigThreadAsync(c => c.Type = ConnectionType.Serial, token).ConfigureAwait(false));
            await hellos.NextAsync(null, token).ConfigureAwait(false);
            _runtime.Config.ConnectionConfigChanged -= OnConnectionChanged;
            SetTurnOff(null);

            var ready = await ReadySetupAsync(token).ConfigureAwait(false);
            if (ready.Mirror is not { } mirror) return ready.Failure;
            if (FromMirror(mirror, 0) is null) return WifiSwitchOutcome.BackOnUsb;   // already off

            AppLog.Info("Wi-Fi off: the controller answered over USB, saving with Wi-Fi off");
            var begun = await BeginSaveAsync(m =>
            {
                var udp = ControllerConfigCodec.Decode(m).EffectiveUdp().Clone();
                udp.Enabled = false;
                return Blob(m, udp);
            }, token).ConfigureAwait(false);
            if (begun.Run is not { } run) return begun.Failure;
            var saved = await run.Save.WaitAsync(token).ConfigureAwait(false);
            return saved.Status == SaveStatus.Confirmed ? WifiSwitchOutcome.BackOnUsb : Failed(saved);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            AppLog.Info("Wi-Fi off: cancelled");
            return WifiSwitchOutcome.Cancelled;
        }
        finally
        {
            _runtime.Config.ConnectionConfigChanged -= OnConnectionChanged;
            SetTurnOff(null);
            Exit();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private bool ConnectedOver(ConnectionType type)
        => _runtime.Config.Config.Connection.Type == type && _runtime.TransportType == type && _runtime.LinkStatus.IsConnected;

    private static Udp WifiOn(WifiSettings s) => new()
    {
        Enabled = true,
        Ssid = s.Ssid ?? "",
        Password = s.Password ?? "",
        PcIp = s.PcIp,
        PcPort = (uint)s.ListenPort,
        LocalPort = (uint)s.ControllerPort,
    };

    private static bool Decodes(byte[]? mirror) => ControllerBannerLogic.Decodes(mirror);

    /// <summary>The mirror with its Wi-Fi section replaced by <paramref name="udp"/>, encoded; null
    /// when the result isn't a valid config (nothing is sent then).</summary>
    private static byte[]? Blob(byte[] mirror, Udp udp)
    {
        try
        {
            var config = ControllerConfigCodec.Decode(mirror);
            config.Udp = udp;
            var v = ControllerConfigValidator.Validate(config, ChipInfo.Esp32);
            if (!v.IsOk)
            {
                AppLog.Info($"Wi-Fi settings not saved: invalid ({v})");
                return null;
            }
            return ControllerConfigCodec.Encode(config);
        }
        catch (ControllerConfigFormatException e)
        {
            AppLog.Info($"Wi-Fi settings not saved: {e.Message}");
            return null;
        }
    }

    private static bool IsNew(HelloMessage hello, uint crc)
        => hello.ConfigState == ControllerConfigState.Stored && hello.ConfigCrc == crc;

    private static WifiSwitchOutcome Failed(ConfigSaveResult result)
    {
        AppLog.Info($"Wi-Fi: the save didn't go through ({result.Status})");
        return WifiSwitchOutcome.SaveFailed;
    }

    /// <summary>Edits the connection config on the config thread and restarts the connection.
    /// Returns <see cref="VolcoRuntime.TransportGeneration"/> as it was just before the edit:
    /// the transports the edit brings are newer. Does nothing once <paramref name="token"/> is
    /// cancelled, even if the edit was already posted.</summary>
    private async Task<long> OnConfigThreadAsync(Action<ConnectionConfig> edit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long before = 0;
        await _configThread(() =>
        {
            if (token.IsCancellationRequested) return;
            before = _runtime.TransportGeneration;
            edit(_runtime.Config.Config.Connection);
            _runtime.Config.RequestSave(connectionChanged: true);
        }).WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return before;
    }

    /// <summary>A save this switch owns, and when the controller accepted it (its OK: the
    /// service's Confirming phase).</summary>
    private sealed class SaveRun
    {
        private readonly TaskCompletionSource _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _owned;

        public Task<ConfigSaveResult> Save { get; private set; } = null!;
        public Task Accepted => _accepted.Task;

        public void Own(Task<ConfigSaveResult> save)
        {
            Save = save;
            _owned = true;
        }

        public void Check(ControllerConfigService service)
        {
            if (_owned && service.Operation == ConfigOperation.Writing && service.Progress is { Phase: SavePhase.Confirming })
                _accepted.TrySetResult();
        }
    }

    /// <summary>Waits for a running config operation (the quiet read a Hello starts, which may
    /// adopt the controller's Wi-Fi fallback into the mirror) to end, up to <see cref="BusyWait"/>,
    /// then returns the mirror when the connected controller runs it.</summary>
    private async Task<(byte[]? Mirror, WifiSwitchOutcome Failure)> ReadySetupAsync(CancellationToken token)
    {
        var service = _runtime.ControllerConfig;
        if (service.Operation != ConfigOperation.Idle && !await WaitForIdleAsync(service, _time.GetUtcNow() + BusyWait, token).ConfigureAwait(false))
            return (null, WifiSwitchOutcome.SaveFailed);
        var mirror = service.Mirror;
        if (!Decodes(mirror))
        {
            AppLog.Info("Wi-Fi: Volco has no saved setup of this controller; nothing sent");
            return (null, WifiSwitchOutcome.SaveFailed);
        }
        if (service.LastHello is not { ConfigState: ControllerConfigState.Stored } hello || hello.ConfigCrc != ControllerConfigCodec.Crc32(mirror!))
        {
            AppLog.Info("Wi-Fi: the controller's setup differs from Volco's saved copy; nothing sent");
            return (null, WifiSwitchOutcome.DifferentSetup);
        }
        return (mirror, default);
    }

    /// <summary>Starts the save of <paramref name="build"/>(mirror) once <see cref="ReadySetupAsync"/>
    /// allows it; an operation that starts in between (Busy) is waited out the same way.</summary>
    private async Task<(SaveRun? Run, byte[]? Blob, WifiSwitchOutcome Failure)> BeginSaveAsync(Func<byte[], byte[]?> build, CancellationToken token)
    {
        var service = _runtime.ControllerConfig;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var ready = await ReadySetupAsync(token).ConfigureAwait(false);
            if (ready.Mirror is not { } mirror) return (null, null, ready.Failure);
            if (build(mirror) is not { } blob) return (null, null, WifiSwitchOutcome.SaveFailed);

            var run = new SaveRun();
            void OnOperation(object? s, EventArgs e) => run.Check(service);
            service.OperationChanged += OnOperation;
            var save = service.SaveAsync(blob, token);
            // SaveAsync claims the operation before its first await: Busy comes back completed.
            if (save.IsCompleted && save.Result.Status == SaveStatus.Busy)
            {
                service.OperationChanged -= OnOperation;
                AppLog.Info("Wi-Fi: a config operation is running; the save waits for it");
                if (!await WaitForIdleAsync(service, _time.GetUtcNow() + BusyWait, token).ConfigureAwait(false))
                    return (null, null, WifiSwitchOutcome.SaveFailed);
                continue;
            }
            run.Own(save);
            run.Check(service);
            _ = save.ContinueWith(_ => service.OperationChanged -= OnOperation, TaskScheduler.Default);
            return (run, blob, default);
        }
    }

    private async Task<bool> WaitForIdleAsync(ControllerConfigService service, DateTimeOffset giveUp, CancellationToken token)
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOperation(object? s, EventArgs e)
        {
            if (service.Operation == ConfigOperation.Idle) idle.TrySetResult();
        }
        service.OperationChanged += OnOperation;
        try
        {
            OnOperation(null, EventArgs.Empty);
            var remaining = giveUp - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero) return idle.Task.IsCompleted;
            using var timeout = new CancellationTokenSource(remaining, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, token);
            try
            {
                await idle.Task.WaitAsync(linked.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return false;
            }
        }
        finally { service.OperationChanged -= OnOperation; }
    }

    /// <summary>Collects the Hellos (this protocol version) that come over a transport of the
    /// watched kind, from its creation until disposed; after <see cref="After"/>, only those of a
    /// transport built after that start number count.</summary>
    private sealed class HelloWatch : IDisposable
    {
        private readonly VolcoRuntime _runtime;
        private readonly ConnectionType _type;
        private readonly TimeProvider _time;
        private long _after;
        private readonly Channel<TransportHello> _hellos = Channel.CreateUnbounded<TransportHello>(new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });

        public HelloWatch(VolcoRuntime runtime, ConnectionType type, TimeProvider time)
        {
            _runtime = runtime;
            _type = type;
            _time = time;
            _runtime.TransportHelloReceived += OnHello;
        }

        /// <summary>From now on only transports with a start number above <paramref name="generation"/> count.</summary>
        public void After(long generation) => Interlocked.Exchange(ref _after, generation);

        private bool Counts(TransportHello h) => h.Generation > Interlocked.Read(ref _after);

        private void OnHello(object? sender, TransportHello hello)
        {
            if (hello.Hello.Version == ProtocolVersion.Current && hello.Type == _type && Counts(hello)) _hellos.Writer.TryWrite(hello);
        }

        /// <summary>The next Hello that counts, or null at <paramref name="deadline"/> (null: no deadline).</summary>
        public async Task<HelloMessage?> NextAsync(DateTimeOffset? deadline, CancellationToken token)
        {
            while (true)
            {
                var next = await ReadAsync(deadline, token).ConfigureAwait(false);
                if (next is null) return null;
                if (Counts(next)) return next.Hello;   // queued before an After: skipped
            }
        }

        private async Task<TransportHello?> ReadAsync(DateTimeOffset? deadline, CancellationToken token)
        {
            if (deadline is not { } end) return await _hellos.Reader.ReadAsync(token).ConfigureAwait(false);
            var remaining = end - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero) return _hellos.Reader.TryRead(out var ready) ? ready : null;
            using var timeout = new CancellationTokenSource(remaining, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, token);
            try
            {
                return await _hellos.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return _hellos.Reader.TryRead(out var last) ? last : null;
            }
        }

        public void Dispose() => _runtime.TransportHelloReceived -= OnHello;
    }

    /// <summary>Reports <see cref="WifiSwitchStep.WaitingForWifi"/> with the time left to
    /// <paramref name="end"/> now and every second, until disposed.</summary>
    private IDisposable? Countdown(IProgress<(WifiSwitchStep Step, TimeSpan? Left)>? progress, DateTimeOffset end)
    {
        if (progress is null) return null;
        void Report()
        {
            var left = end - _time.GetUtcNow();
            progress.Report((WifiSwitchStep.WaitingForWifi, left > TimeSpan.Zero ? left : TimeSpan.Zero));
        }
        Report();
        return _time.CreateTimer(_ => Report(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private bool TryEnter()
    {
        if (_disposed.IsCancellationRequested || Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;
        BusyChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Exit()
    {
        Volatile.Write(ref _busy, 0);
        if (!_disposed.IsCancellationRequested) BusyChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetTurnOff(CancellationTokenSource? turnOff)
    {
        if (Interlocked.Exchange(ref _turnOff, turnOff) == turnOff) return;
        if (!_disposed.IsCancellationRequested) BusyChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void Cancel(CancellationTokenSource? cts)
    {
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* that operation has ended */ }
    }

    /// <summary>Cancels the running operation (a pending turn-off included). The transport is left
    /// as it is; nothing is edited afterwards.</summary>
    public void Dispose() => Cancel(_disposed);
}
