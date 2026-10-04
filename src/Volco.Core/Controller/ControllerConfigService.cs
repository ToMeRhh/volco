using System.Threading.Channels;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Diagnostics;

namespace Volco.Core.Controller;

public enum ConfigOperation { Idle, Reading, Writing }

/// <summary>Failed covers no complete reply after 3 attempts, and (with adopt) bytes this app
/// can't decode (spec §8.3: "Couldn't read the controller's setup").</summary>
public enum ReadStatus { Ok, Failed, Cancelled, Busy }

/// <param name="Blob">The controller's stored bytes (empty when it has none); null unless Ok.</param>
public sealed record ConfigReadResult(ReadStatus Status, byte[]? Blob = null);

public enum SaveStatus
{
    /// <summary>The controller restarted with the new setup; the mirror holds it.</summary>
    Confirmed,
    /// <summary>TOO_LARGE, DECODE_FAILED, INVALID or STORAGE_FAILED: see Code, Item and Field.</summary>
    Rejected,
    /// <summary>"The controller restarted but isn't running the new setup."</summary>
    NotRunningNewSetup,
    /// <summary>"The controller didn't come back after restarting."</summary>
    DidNotComeBack,
    /// <summary>"The controller didn't answer."</summary>
    NoAnswer,
    /// <summary>"Connection lost while saving. Reconnect to see which setup the controller has."</summary>
    ConnectionLost,
    Busy,
    Cancelled,
}

public sealed record ConfigSaveResult(SaveStatus Status, ConfigResultCode? Code = null, byte Item = 0xFF, byte Field = 0xFF);

public enum SavePhase { Sending, WaitingForImpliedOk, Confirming }

/// <param name="Attempt">The attempt (1..3): the one being sent, or the one that got the OK.</param>
public sealed record SaveProgress(SavePhase Phase, int Attempt);

/// <summary>
/// The app side of the config session (spec §5.3, §5.4, §8.2, §8.3): reads the controller's stored
/// config, saves a new one with retries, implied OK and confirmation, keeps the mirror
/// (<see cref="AppConfig.ControllerConfigBlob"/>), and computes the banner after every Hello.
/// Owned by <c>VolcoRuntime</c>, so closing a window never cancels a save.
/// <para>
/// Threading: <see cref="OnMessage"/> and <see cref="OnConnectionReplaced"/> are called on
/// transport threads. They only record the Hello and post an event to the running operation's
/// channel, whose reader never continues synchronously, so no operation code runs on a transport
/// thread. Shared state is guarded by <c>_gate</c>; events are raised outside it, on any thread.
/// </para>
/// </summary>
public sealed class ControllerConfigService : IDisposable
{
    public static class Timeouts
    {
        public static readonly TimeSpan ReadWait = TimeSpan.FromSeconds(2);
        public const int ReadAttempts = 3;
        public static readonly TimeSpan ResultWait = TimeSpan.FromSeconds(3);
        public const int WriteAttempts = 3;
        public static readonly TimeSpan Confirm = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan ConfirmUdp = TimeSpan.FromSeconds(20);
    }

    private abstract record Event;
    private sealed record MessageEvent(ControllerMessage Message) : Event;
    private sealed record ReplacedEvent : Event;

    private readonly object _gate = new();
    private readonly object _bannerGate = new();   // serialises UpdateBanner (never taken inside _gate)
    private readonly ConfigManager _config;
    private readonly IConfigSessionLink _link;
    private readonly TimeProvider _time;
    private readonly Func<string?> _bundledFirmware;

    // Guarded by _gate.
    private HelloMessage? _lastHello;
    private ConfigOperation _operation;
    private SaveProgress? _progress;
    private Channel<Event>? _events;
    private byte _seq;
    private bool _readFailed;
    private (byte, ControllerConfigState, uint)? _autoReadKey;
    private ControllerBanner _banner = ControllerBanner.Empty;
    private bool _disposed;

    private int _waits;

    /// <param name="bundledFirmwareVersion">The build id Volco carries (stage 6); null → none.</param>
    public ControllerConfigService(ConfigManager config, IConfigSessionLink link, TimeProvider? time = null,
                                   Func<string?>? bundledFirmwareVersion = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _link = link ?? throw new ArgumentNullException(nameof(link));
        _time = time ?? TimeProvider.System;
        _bundledFirmware = bundledFirmwareVersion ?? (() => null);
    }

    public HelloMessage? LastHello { get { lock (_gate) return _lastHello; } }
    public ConfigOperation Operation { get { lock (_gate) return _operation; } }
    public SaveProgress? Progress { get { lock (_gate) return _progress; } }
    public ControllerBanner Banner { get { lock (_gate) return _banner; } }

    /// <summary>The mirror's bytes, or null when there is none (or it isn't valid base64).</summary>
    public byte[]? Mirror
    {
        get
        {
            var text = _config.Config.ControllerConfigBlob;
            if (string.IsNullOrEmpty(text)) return null;
            try { return Convert.FromBase64String(text); }
            catch (FormatException) { return null; }
        }
    }

    /// <summary>Operation, or a save's phase, changed. Any thread.</summary>
    public event EventHandler? OperationChanged;
    /// <summary>The mirror was replaced (a confirmed save or an adopted read). Any thread.</summary>
    public event EventHandler? MirrorChanged;
    /// <summary>The banner's value changed. Any thread.</summary>
    public event EventHandler? BannerChanged;

    /// <summary>Tests: how many times an operation has started waiting for an event.</summary>
    internal int Waits => Volatile.Read(ref _waits);

    // ── Inbound ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Hello, ConfigData and ConfigResult from the current connection (already filtered
    /// for stale senders by the runtime). Any thread.</summary>
    public void OnMessage(ControllerMessage message)
    {
        if (message is not (HelloMessage or ConfigDataMessage or ConfigResultMessage)) return;
        lock (_gate)
        {
            if (_disposed) return;
            if (message is HelloMessage hello)
            {
                if (_lastHello is null || Key(_lastHello) != Key(hello))
                {
                    _readFailed = false;
                    _autoReadKey = null;
                }
                _lastHello = hello;
            }
            _events?.Writer.TryWrite(new MessageEvent(message));
        }
        if (message is HelloMessage)
        {
            UpdateBanner();
            MaybeAutoRead();
        }
    }

    /// <summary>The runtime replaced (or disposed) the connection: an operation in flight fails,
    /// and the last Hello no longer describes the controller. A plain Detach/Attach of the same
    /// connection is not a replacement.</summary>
    public void OnConnectionReplaced()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _lastHello = null;
            _readFailed = false;
            _autoReadKey = null;
            _events?.Writer.TryWrite(new ReplacedEvent());
        }
        UpdateBanner();
    }

    // ── Read (spec §5.3) ─────────────────────────────────────────────────────────────────────

    /// <summary>Reads the controller's stored config. With <paramref name="adopt"/>, a non-empty
    /// result becomes the mirror.</summary>
    public async Task<ConfigReadResult> ReadAsync(bool adopt, CancellationToken ct = default)
    {
        if (!TryBegin(ConfigOperation.Reading, out var events, out var startHello)) return new(ReadStatus.Busy);
        ConfigReadResult result;
        try
        {
            result = await RunReadAsync(events, startHello, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = new(ReadStatus.Cancelled);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Config read failed: {ex}");
            result = new(ReadStatus.Failed);
        }
        if (result.Status == ReadStatus.Ok && adopt && result.Blob is { Length: > 0 } blob)
        {
            // Bytes this app can't decode (e.g. written by a newer app) never replace the mirror.
            if (ControllerBannerLogic.Decodes(blob)) SetMirror(blob);
            else
            {
                AppLog.Error($"Config read: {blob.Length} bytes this app can't decode; not adopted");
                result = new(ReadStatus.Failed);
            }
        }
        End(startHello, readStatus: result.Status);
        return result;
    }

    /// <summary>The banner's Retry: a read that adopts.</summary>
    public Task<ConfigReadResult> RetryReadAsync() => ReadAsync(adopt: true);

    private async Task<ConfigReadResult> RunReadAsync(Channel<Event> events, HelloMessage? start, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= Timeouts.ReadAttempts; attempt++)
        {
            var seq = NextSeq();
            var deadline = _time.GetUtcNow() + Timeouts.ReadWait;
            _link.SendRaw(ProtocolEncoder.EncodeConfigRead(seq));  // not attached: the wait times out

            var data = new List<byte>();
            int? total = null;
            uint crc = 0;
            while (true)
            {
                var ev = await NextAsync(events, deadline, ct).ConfigureAwait(false);
                if (ev is null) break;                                   // timed out: next attempt
                if (ev is ReplacedEvent) return new(ReadStatus.Cancelled);
                var msg = ((MessageEvent)ev).Message;
                if (msg is HelloMessage hello)
                {
                    // A Hello equal to the one the read started under (one a ConfigRead after a
                    // host timeout provokes) is ignored; a different one cancels the read.
                    if (start is not null && Key(hello) == Key(start)) continue;
                    return new(ReadStatus.Cancelled);
                }
                if (msg is not ConfigDataMessage d || d.Seq != seq) continue;
                if (total is null) (total, crc) = (d.Total, d.Crc32);
                if (d.Total != total || d.Crc32 != crc || d.Offset != data.Count || data.Count + d.Data.Length > total) break;
                data.AddRange(d.Data);
                deadline = _time.GetUtcNow() + Timeouts.ReadWait;
                if (data.Count < total) continue;
                var blob = data.ToArray();
                if (blob.Length == 0 || ControllerConfigCodec.Crc32(blob) == crc) return new(ReadStatus.Ok, blob);
                break;                                                   // CRC mismatch: next attempt
            }
        }
        return new(ReadStatus.Failed);
    }

    // ── Save (spec §5.4) ─────────────────────────────────────────────────────────────────────

    /// <summary>Saves <paramref name="blob"/> to the controller. <paramref name="ct"/> is honoured
    /// only before the first burst is sent.</summary>
    public async Task<ConfigSaveResult> SaveAsync(byte[] blob, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Length is 0 or > ControllerConfigCodec.MaxBlob)
            throw new ArgumentException($"A config blob is 1..{ControllerConfigCodec.MaxBlob} bytes, not {blob.Length}.", nameof(blob));
        if (ct.IsCancellationRequested) return new(SaveStatus.Cancelled);
        if (!TryBegin(ConfigOperation.Writing, out var events, out var startHello)) return new(SaveStatus.Busy);
        ConfigSaveResult result;
        try
        {
            result = await RunSaveAsync(events, blob).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Config save failed: {ex}");
            result = new(SaveStatus.NoAnswer);
        }
        if (result.Status == SaveStatus.Confirmed) SetMirror(blob);
        End(startHello, readStatus: null);
        AppLog.Info($"Config save: {result.Status}{(result.Code is { } c ? $" {c} item {result.Item} field {result.Field}" : "")}");
        return result;
    }

    private async Task<ConfigSaveResult> RunSaveAsync(Channel<Event> events, byte[] blob)
    {
        var crc = ControllerConfigCodec.Crc32(blob);
        bool Confirms(HelloMessage h) =>
            h.Version == ProtocolVersion.Current && h.ConfigState == ControllerConfigState.Stored && h.ConfigCrc == crc;

        bool ok = false;
        int attempt;
        for (attempt = 1; attempt <= Timeouts.WriteAttempts; attempt++)
        {
            SetProgress(new(SavePhase.Sending, attempt));
            var seq = NextSeq();
            var deadline = _time.GetUtcNow() + Timeouts.ResultWait;
            _link.SendRaw(ProtocolEncoder.EncodeConfigWrite(seq, blob));  // not attached: unanswered

            while (true)
            {
                var ev = await NextAsync(events, deadline, CancellationToken.None).ConfigureAwait(false);
                if (ev is null) break;                                    // no result: next attempt
                if (ev is ReplacedEvent) return new(SaveStatus.ConnectionLost);
                var msg = ((MessageEvent)ev).Message;
                if (msg is HelloMessage hello)
                {
                    if (Confirms(hello)) return new(SaveStatus.Confirmed);   // implied OK
                    break;                                                   // e.g. reset by hand: next attempt
                }
                if (msg is not ConfigResultMessage r || r.Seq != seq) continue;
                if (r.Code == ConfigResultCode.Ok) { ok = true; break; }
                if (r.Code is ConfigResultCode.BadSequence or ConfigResultCode.BadCrc) break;
                return new(SaveStatus.Rejected, r.Code, r.Item, r.Field);
            }
            if (ok) break;
        }

        var confirmTimeout = _link.ConnectionType == ConnectionType.Udp ? Timeouts.ConfirmUdp : Timeouts.Confirm;
        var confirmDeadline = _time.GetUtcNow() + confirmTimeout;
        SetProgress(new(ok ? SavePhase.Confirming : SavePhase.WaitingForImpliedOk, Math.Min(attempt, Timeouts.WriteAttempts)));
        while (true)
        {
            var ev = await NextAsync(events, confirmDeadline, CancellationToken.None).ConfigureAwait(false);
            if (ev is null) return new(ok ? SaveStatus.DidNotComeBack : SaveStatus.NoAnswer);
            if (ev is ReplacedEvent) return new(SaveStatus.ConnectionLost);
            if (((MessageEvent)ev).Message is not HelloMessage hello) continue;
            if (Confirms(hello)) return new(SaveStatus.Confirmed);
            if (ok) return new(SaveStatus.NotRunningNewSetup);
            // Waiting for an implied OK after three unanswered attempts: only a confirming Hello counts.
        }
    }

    // ── Operation plumbing ───────────────────────────────────────────────────────────────────

    private bool TryBegin(ConfigOperation operation, out Channel<Event> events, out HelloMessage? startHello)
    {
        lock (_gate)
        {
            events = null!;
            startHello = _lastHello;
            if (_disposed || _operation != ConfigOperation.Idle) return false;
            _operation = operation;
            _progress = null;
            events = _events = Channel.CreateUnbounded<Event>(new UnboundedChannelOptions
            {
                SingleReader = true,
                AllowSynchronousContinuations = false,
            });
        }
        RaiseOperationChanged();
        UpdateBanner();
        return true;
    }

    /// <param name="readStatus">A read's outcome; null for a save.</param>
    private void End(HelloMessage? startHello, ReadStatus? readStatus)
    {
        lock (_gate)
        {
            _operation = ConfigOperation.Idle;
            _progress = null;
            _events = null;
            // A failed read marks the Hello it ran under, unless a different Hello arrived since.
            if (readStatus == ReadStatus.Failed && _lastHello is not null
                && (startHello is null || Key(_lastHello) == Key(startHello)))
                _readFailed = true;
            else if (readStatus == ReadStatus.Ok)
                _readFailed = false;   // a cancelled read keeps the failed state and its Retry
        }
        RaiseOperationChanged();
        UpdateBanner();
        MaybeAutoRead();
    }

    /// <summary>The next event, or null at <paramref name="deadline"/>.</summary>
    private async Task<Event?> NextAsync(Channel<Event> events, DateTimeOffset deadline, CancellationToken ct)
    {
        var remaining = deadline - _time.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
            return events.Reader.TryRead(out var ready) ? ready : null;
        using var timeout = new CancellationTokenSource(remaining, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct);
        Interlocked.Increment(ref _waits);
        try
        {
            return await events.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private byte NextSeq()
    {
        lock (_gate) return ++_seq;
    }

    private void SetProgress(SaveProgress progress)
    {
        lock (_gate) _progress = progress;
        RaiseOperationChanged();
    }

    private void SetMirror(byte[] blob)
    {
        _config.Config.ControllerConfigBlob = Convert.ToBase64String(blob);
        _config.RequestSave();
        if (!IsDisposed) MirrorChanged?.Invoke(this, EventArgs.Empty);
        UpdateBanner();
    }

    /// <summary>Spec §8.3: reads start automatically only in the "stored, no mirror" row, once per
    /// (version, configState, configCrc).</summary>
    private void MaybeAutoRead()
    {
        lock (_gate)
        {
            if (_disposed || _operation != ConfigOperation.Idle || _lastHello is not { } h) return;
            if (h.Version != ProtocolVersion.Current || h.ConfigState != ControllerConfigState.Stored) return;
            if (ControllerBannerLogic.Decodes(Mirror) || _autoReadKey == Key(h) || _readFailed) return;
            _autoReadKey = Key(h);
        }
        _ = AutoReadAsync();
    }

    private async Task AutoReadAsync()
    {
        try
        {
            var r = await ReadAsync(adopt: true).ConfigureAwait(false);
            if (r.Status == ReadStatus.Busy)
            {
                // A user operation started first: its End() re-runs MaybeAutoRead for this Hello.
                lock (_gate) _autoReadKey = null;
                MaybeAutoRead();
                return;
            }
            AppLog.Info($"Automatic config read: {r.Status}{(r.Blob is { } b ? $", {b.Length} bytes" : "")}");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Automatic config read failed: {ex}");
        }
    }

    /// <summary>Recomputes the banner. Serialised, and every input is read inside the same
    /// critical section, so a slower computation from older inputs can't be stored last.</summary>
    private void UpdateBanner()
    {
        bool changed;
        lock (_bannerGate)
        {
            var inputs = new BannerInputs(null, Mirror, ConfigOperation.Idle, false, _link.ConnectionType, _bundledFirmware());
            lock (_gate)
            {
                if (_disposed) return;
                inputs = inputs with { Hello = _lastHello, Operation = _operation, ReadFailed = _readFailed };
            }
            var banner = ControllerBannerLogic.Compute(inputs);
            lock (_gate)
            {
                changed = banner != _banner;
                _banner = banner;
            }
        }
        if (changed) BannerChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseOperationChanged()
    {
        if (!IsDisposed) OperationChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool IsDisposed { get { lock (_gate) return _disposed; } }

    private static (byte, ControllerConfigState, uint) Key(HelloMessage h) => (h.Version, h.ConfigState, h.ConfigCrc);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _events?.Writer.TryWrite(new ReplacedEvent());
            _disposed = true;
        }
    }
}
