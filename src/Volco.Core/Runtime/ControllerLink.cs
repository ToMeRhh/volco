using Volco.Core.Communication;
using Volco.Core.Diagnostics;

namespace Volco.Core.Runtime;

public sealed record ControllerLinkOptions(TimeSpan HelloRetryInterval, TimeSpan HeartbeatInterval, TimeSpan TickInterval)
{
    public static ControllerLinkOptions Default { get; } =
        new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(250));
}

/// <summary>
/// One controller session (spec §3.4, §5.5): HelloRequest until Hello, the controller state,
/// keepalive, per-button dedup, and the only path frames take to the controller.
/// <para>
/// A single lock (<c>_gate</c>) is held across compute → dedup → enqueue, so frames are queued in
/// the order their state was computed and a Begin … Commit burst is enqueued contiguously. The
/// frames are then sent <b>outside</b> that lock by a single-consumer outbox: whichever thread
/// finds the outbox idle drains it, and every other thread just enqueues and returns. So a slow or
/// re-entrant transport (one that raises <c>StatusChanged</c> from <c>Send</c>, or whose own lock
/// is held by a thread waiting for ours) can never deadlock the link, and a Send that throws is
/// logged and dropped. Events are raised outside every lock. For a single-threaded caller the
/// frames have all been sent by the time a public method returns.
/// </para>
/// <para>
/// A mode change is announced for a few ticks (the LED blink and the screen's mode flash):
/// <c>compute</c> gets the announce step (0 at the change, +1 per tick, null once it is over), and
/// each tick sends what changed, sliding screens the way the mode change did.
/// </para>
/// </summary>
public sealed class ControllerLink : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<int?, IReadOnlyList<ButtonDisplay?>> _compute;
    private readonly int _announceSteps;
    private readonly ControllerLinkOptions _options;
    private readonly Timer _timer;

    // Outbox: guarded by _outLock only (never take _gate while holding it).
    private readonly object _outLock = new();
    private readonly Queue<(IControllerConnection Connection, byte[] Frame)> _outbox = new();
    private bool _draining;

    // Guarded by _gate. _connection is also read lock-free by the drain loop.
    private volatile IControllerConnection? _connection;
    private ControllerState _state = ControllerState.NoController;
    private ButtonDisplay?[] _sent = Array.Empty<ButtonDisplay?>();
    private long _lastSendMs;
    private long _lastHelloRequestMs;
    private int? _announceStep;
    private ScreenTransition _announceTransition;
    private bool _disposed;

    /// <param name="compute">What every controller button should show now (ControllerDisplay).
    /// Called with the lock held; must only read. If it throws, the push is skipped and logged.</param>
    public ControllerLink(Func<IReadOnlyList<ButtonDisplay?>> compute, ControllerLinkOptions? options = null)
        : this(_ => compute(), 0, options) { }

    /// <param name="compute">As above, given the announce step (null when no mode change is
    /// being announced).</param>
    /// <param name="announceSteps">How many ticks a mode change is announced for.</param>
    public ControllerLink(Func<int?, IReadOnlyList<ButtonDisplay?>> compute, int announceSteps, ControllerLinkOptions? options = null)
    {
        _compute = compute;
        _announceSteps = announceSteps;
        _options = options ?? ControllerLinkOptions.Default;
        _timer = new Timer(_ => Tick(), null, _options.TickInterval, _options.TickInterval);
    }

    public ControllerState State { get { lock (_gate) return _state; } }

    /// <summary>The controller state changed. Any thread.</summary>
    public event EventHandler<ControllerState>? StateChanged;

    /// <summary>A Hello with this app's version arrived. Raised before the full push it triggers,
    /// so a handler can store the controller info the push is computed from. Any thread.</summary>
    public event EventHandler<HelloMessage>? HelloReceived;

    /// <summary>The transport just connected: start a session on it.</summary>
    public void Attach(IControllerConnection connection)
    {
        ControllerState? changed;
        lock (_gate)
        {
            if (_disposed) return;
            ClearOutbox();
            _connection = connection;
            _sent = Array.Empty<ButtonDisplay?>();
            _announceStep = null;
            changed = SetState(ControllerState.AwaitingHello);
            SendHelloRequest();
        }
        Raise(changed);
        Drain();
    }

    /// <summary>The transport went away.</summary>
    public void Detach()
    {
        ControllerState? changed;
        lock (_gate)
        {
            if (_disposed) return;
            changed = Detached();
        }
        Raise(changed);
    }

    /// <summary>Handles the session messages (Hello; Alive needs nothing beyond arriving).</summary>
    public void OnMessage(ControllerMessage message)
    {
        if (message is not HelloMessage hello) return;
        var ok = hello.Version == ProtocolVersion.Current;
        lock (_gate)
        {
            if (_connection is null || _disposed) return;
        }
        if (!ok) AppLog.Error($"Controller speaks protocol v{hello.Version}; this app speaks v{ProtocolVersion.Current}");
        else HelloReceived?.Invoke(this, hello);

        ControllerState? changed;
        lock (_gate)
        {
            if (_connection is null || _disposed) return;
            _sent = Array.Empty<ButtonDisplay?>();
            _announceStep = null;
            changed = SetState(ok ? ControllerState.Ready : ControllerState.VersionMismatch);
            if (ok) PushLocked(ScreenTransition.None, everything: true);
        }
        Raise(changed);
        Drain();
    }

    /// <summary>Sends a whole burst (a config read or write, spec §5.3/§5.4) as one transport
    /// send. Needs only an attached connection, not Ready. False when none is attached.</summary>
    public bool SendRaw(byte[] burst)
    {
        lock (_gate)
        {
            if (_connection is null || _disposed) return false;
            Send(burst);
        }
        Drain();
        return true;
    }

    /// <summary>Sends every button whose display changed, with no transition.</summary>
    public void Push()
    {
        lock (_gate)
        {
            if (_state == ControllerState.Ready) PushLocked(ScreenTransition.None, everything: false);
        }
        Drain();
    }

    /// <summary>Re-sends every button with no transition (e.g. the live mode was deleted).</summary>
    public void PushAll()
    {
        lock (_gate)
        {
            if (_state == ControllerState.Ready) PushLocked(ScreenTransition.None, everything: true);
        }
        Drain();
    }

    /// <summary>A mode change: every screen slides (even unchanged ones) and every LED is
    /// re-sent; wrapped in Begin/Commit when <paramref name="synchronized"/>. Starts the announce
    /// steps.</summary>
    public void PushModeChange(ScreenTransition transition, bool synchronized)
    {
        lock (_gate)
        {
            if (_state != ControllerState.Ready) return;
            _announceStep = _announceSteps > 0 ? 0 : null;
            _announceTransition = transition;
            // Computed before Begin is queued, so a compute that throws leaves no open burst.
            if (TryCompute() is not { } displays) return;
            if (synchronized) Send(ProtocolEncoder.EncodeBegin());
            EnsureCapacity(displays.Count);
            for (int i = 0; i < displays.Count; i++)
                if (displays[i] is ScreenDisplay s) { Send(ProtocolEncoder.EncodeScreen(i, transition, s.Content)); _sent[i] = s; }
            for (int i = 0; i < displays.Count; i++)
                if (displays[i] is LedDisplay l) { Send(ProtocolEncoder.EncodeLed(i, l.Value)); _sent[i] = l; }
            if (synchronized) Send(ProtocolEncoder.EncodeCommit());
        }
        Drain();
    }

    private void PushLocked(ScreenTransition transition, bool everything)
    {
        if (TryCompute() is not { } displays) return;
        EnsureCapacity(displays.Count);
        for (int i = 0; i < displays.Count; i++)
        {
            var d = displays[i];
            if (d is null || (!everything && Equals(_sent[i], d))) continue;
            Send(d switch
            {
                LedDisplay l => ProtocolEncoder.EncodeLed(i, l.Value),
                ScreenDisplay s => ProtocolEncoder.EncodeScreen(i, transition, s.Content),
                _ => throw new InvalidOperationException(d.GetType().Name),
            });
            _sent[i] = d;
        }
    }

    /// <summary>Caller holds _gate. Runs <c>_compute</c>, which reads live config lists the UI
    /// thread may be mutating, so it can throw. Callers include timer and transport-receive
    /// threads, where an unhandled exception would end the process: a failure is logged and
    /// returns null, the caller sends nothing and leaves <c>_sent</c> alone, and the next push
    /// sends whatever differs.</summary>
    private IReadOnlyList<ButtonDisplay?>? TryCompute()
    {
        try { return _compute(_announceStep); }
        catch (Exception ex)
        {
            AppLog.Error($"Controller display compute failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private void EnsureCapacity(int count)
    {
        if (_sent.Length < count) Array.Resize(ref _sent, count);
    }

    private void Tick()
    {
        try
        {
            lock (_gate)
            {
                if (_connection is null || _disposed) return;
                if (_state == ControllerState.Ready && _announceStep is { } step)
                {
                    _announceStep = step + 1 < _announceSteps ? step + 1 : null;
                    PushLocked(_announceTransition, everything: false);
                }
                var now = Environment.TickCount64;
                if (_state == ControllerState.AwaitingHello
                    && now - _lastHelloRequestMs >= (long)_options.HelloRetryInterval.TotalMilliseconds)
                    SendHelloRequest();
                else if (_state == ControllerState.Ready
                         && now - _lastSendMs >= (long)_options.HeartbeatInterval.TotalMilliseconds)
                    Send(ProtocolEncoder.EncodeHeartbeat());
            }
            Drain();
        }
        catch (Exception ex)
        {
            AppLog.Error($"Controller link tick failed: {ex.Message}");
        }
    }

    private void SendHelloRequest()
    {
        Send(ProtocolEncoder.EncodeHelloRequest());
        _lastHelloRequestMs = Environment.TickCount64;
    }

    /// <summary>Caller holds _gate. Queues the frame; Drain() sends it.</summary>
    private void Send(byte[] frame)
    {
        if (_connection is { } c)
        {
            lock (_outLock) _outbox.Enqueue((c, frame));
        }
        _lastSendMs = Environment.TickCount64;
    }

    /// <summary>Sends queued frames. Only one thread drains at a time; the others return and
    /// their frames are picked up by the drainer. Called with no lock held.</summary>
    private void Drain()
    {
        lock (_outLock)
        {
            if (_draining) return;
            _draining = true;
        }
        while (true)
        {
            (IControllerConnection Connection, byte[] Frame) item;
            lock (_outLock)
            {
                if (_outbox.Count == 0) { _draining = false; return; }
                item = _outbox.Dequeue();
            }
            if (!ReferenceEquals(item.Connection, _connection)) continue;   // stale attachment
            try { item.Connection.Send(item.Frame); }
            catch (Exception ex) { AppLog.Error($"Controller send failed: {ex.Message}"); }
        }
    }

    private void ClearOutbox()
    {
        lock (_outLock) _outbox.Clear();
    }

    /// <summary>Caller holds _gate.</summary>
    private ControllerState? Detached()
    {
        _connection = null;
        _sent = Array.Empty<ButtonDisplay?>();
        _announceStep = null;
        ClearOutbox();
        return SetState(ControllerState.NoController);
    }

    /// <summary>Caller holds _gate. Returns the new state when it changed.</summary>
    private ControllerState? SetState(ControllerState state)
    {
        if (_state == state) return null;
        _state = state;
        return state;
    }

    private void Raise(ControllerState? changed)
    {
        if (changed is { } s) StateChanged?.Invoke(this, s);
    }

    public void Dispose()
    {
        ControllerState? changed;
        lock (_gate)
        {
            if (_disposed) return;
            changed = Detached();
            _disposed = true;
        }
        _timer.Dispose();
        Raise(changed);
    }
}
