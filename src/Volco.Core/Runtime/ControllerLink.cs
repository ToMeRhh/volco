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
/// keepalive, per-button dedup, and the only path frames take to the controller. A single lock
/// is held across compute → dedup → send, so frames always leave in the order their state was
/// computed and a Begin … Commit burst is never interleaved. Events are raised outside it.
/// </summary>
public sealed class ControllerLink : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<IReadOnlyList<ButtonDisplay?>> _compute;
    private readonly ControllerLinkOptions _options;
    private readonly Timer _timer;

    // Guarded by _gate.
    private IControllerConnection? _connection;
    private ControllerState _state = ControllerState.NoController;
    private ButtonDisplay?[] _sent = Array.Empty<ButtonDisplay?>();
    private long _lastSendMs;
    private long _lastHelloRequestMs;
    private bool _disposed;

    /// <param name="compute">What every controller button should show now (ControllerDisplay).
    /// Called with the lock held; must only read.</param>
    public ControllerLink(Func<IReadOnlyList<ButtonDisplay?>> compute, ControllerLinkOptions? options = null)
    {
        _compute = compute;
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
            _connection = connection;
            _sent = Array.Empty<ButtonDisplay?>();
            changed = SetState(ControllerState.AwaitingHello);
            SendHelloRequest();
        }
        Raise(changed);
    }

    /// <summary>The transport went away.</summary>
    public void Detach()
    {
        ControllerState? changed;
        lock (_gate)
        {
            _connection = null;
            _sent = Array.Empty<ButtonDisplay?>();
            changed = SetState(ControllerState.NoController);
        }
        Raise(changed);
    }

    /// <summary>Handles the session messages (Hello; Alive needs nothing beyond arriving).</summary>
    public void OnMessage(ControllerMessage message)
    {
        if (message is not HelloMessage hello) return;
        var ok = hello.Version == ProtocolVersion.Current;
        if (!ok) AppLog.Error($"Controller speaks protocol v{hello.Version}; this app speaks v{ProtocolVersion.Current}");
        else HelloReceived?.Invoke(this, hello);

        ControllerState? changed;
        lock (_gate)
        {
            if (_connection is null || _disposed) return;
            _sent = Array.Empty<ButtonDisplay?>();
            changed = SetState(ok ? ControllerState.Ready : ControllerState.VersionMismatch);
            if (ok) PushLocked(ScreenTransition.None, everything: true);
        }
        Raise(changed);
    }

    /// <summary>Sends every button whose display changed, with no transition.</summary>
    public void Push()
    {
        lock (_gate)
        {
            if (_state == ControllerState.Ready) PushLocked(ScreenTransition.None, everything: false);
        }
    }

    /// <summary>Re-sends every button with no transition (e.g. the live preset was deleted).</summary>
    public void PushAll()
    {
        lock (_gate)
        {
            if (_state == ControllerState.Ready) PushLocked(ScreenTransition.None, everything: true);
        }
    }

    /// <summary>A preset change: every screen slides (even unchanged ones) and every LED is
    /// re-sent; wrapped in Begin/Commit when <paramref name="synchronized"/>.</summary>
    public void PushPresetChange(ScreenTransition transition, bool synchronized)
    {
        lock (_gate)
        {
            if (_state != ControllerState.Ready) return;
            if (synchronized) Send(ProtocolEncoder.EncodeBegin());
            var displays = _compute();
            EnsureCapacity(displays.Count);
            for (int i = 0; i < displays.Count; i++)
                if (displays[i] is ScreenDisplay s) { Send(ProtocolEncoder.EncodeScreen(i, transition, s.Content)); _sent[i] = s; }
            for (int i = 0; i < displays.Count; i++)
                if (displays[i] is LedDisplay l) { Send(ProtocolEncoder.EncodeLed(i, l.Value)); _sent[i] = l; }
            if (synchronized) Send(ProtocolEncoder.EncodeCommit());
        }
    }

    private void PushLocked(ScreenTransition transition, bool everything)
    {
        var displays = _compute();
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

    private void EnsureCapacity(int count)
    {
        if (_sent.Length < count) Array.Resize(ref _sent, count);
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_connection is null || _disposed) return;
            var now = Environment.TickCount64;
            if (_state == ControllerState.AwaitingHello
                && now - _lastHelloRequestMs >= (long)_options.HelloRetryInterval.TotalMilliseconds)
                SendHelloRequest();
            else if (_state == ControllerState.Ready
                     && now - _lastSendMs >= (long)_options.HeartbeatInterval.TotalMilliseconds)
                Send(ProtocolEncoder.EncodeHeartbeat());
        }
    }

    private void SendHelloRequest()
    {
        Send(ProtocolEncoder.EncodeHelloRequest());
        _lastHelloRequestMs = Environment.TickCount64;
    }

    /// <summary>Caller holds _gate.</summary>
    private void Send(byte[] frame)
    {
        _connection?.Send(frame);
        _lastSendMs = Environment.TickCount64;
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
        lock (_gate)
        {
            _disposed = true;
            _connection = null;
        }
        _timer.Dispose();
    }
}
