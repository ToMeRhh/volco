using Volco.Core.Diagnostics;

namespace Volco.Core.Communication;

/// <summary>How long Automatic waits on each port, and how often it looks at the port list.</summary>
/// <param name="PerPort">How long a port has to send its Hello: long enough for an ESP32 that the
/// open resets to boot and say it.</param>
/// <param name="PortListPoll">How often the port list is read while no port has answered.</param>
public sealed record AutoSerialOptions(TimeSpan PerPort, TimeSpan PortListPoll)
{
    public static AutoSerialOptions Default { get; } = new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
}

/// <summary>
/// The Automatic USB port (spec §10.2): tries the candidate ports (<see cref="SerialPortOrder"/>)
/// one at a time until one answers with a Hello, and then stays on it.
///
/// A round opens each candidate in turn. A port that doesn't open (busy or gone) is dropped at
/// once, so another app's port is never held; an open one carries messages and sends both ways
/// until its Hello (locked: <see cref="PortFound"/>) or until <see cref="AutoSerialOptions.PerPort"/>
/// passes (closed, next). A round with no answer keeps the best candidate open (settled; a later
/// Hello still locks) or none. Each port is tried once per change of the port list, and again when
/// the locked port goes, never in an endless cycle.
///
/// <see cref="Status"/> is <see cref="TransportStatus.Closed"/> only with no candidates, else
/// <see cref="TransportStatus.Open"/>: the runtime's HelloRequests and timeouts carry on across
/// the ports, and it never sees a <see cref="TransportStatus.Failed"/>.
/// </summary>
public sealed class AutoSerialConnection : IControllerConnection
{
    private enum Mode { Idle, Probing, Settled, Locked }

    private readonly ISerialPortInfo _ports;
    private readonly bool _haveInfo;
    private readonly Func<string?> _lastPort;
    private readonly Func<string, IControllerConnection> _open;
    private readonly AutoSerialOptions _options;
    private readonly TimeProvider _time;

    // Guards everything below. Events are queued in _pending under it and raised by Flush once it
    // is released, as SerialConnection raises its own outside its callbacks' work. Monitor is
    // reentrant: an inner connection may raise StatusChanged from inside its Connect().
    private readonly object _gate = new();
    private readonly List<Action> _pending = new();

    private IReadOnlyList<SerialPortDescription> _listed = [];
    private IReadOnlyList<SerialPortDescription> _candidates = [];
    private int _next;
    private IControllerConnection? _current;
    private string? _currentName;
    private string? _settledName;
    private Mode _mode = Mode.Idle;
    private ITimer? _perPortTimer;
    private ITimer? _pollTimer;
    private bool _started;
    private bool _disposed;

    /// <param name="ports">The system's serial ports.</param>
    /// <param name="haveInfo">Whether <paramref name="ports"/> knows the USB ids, so that only
    /// USB ports are tried.</param>
    /// <param name="lastPort">The port that answered last time (<c>connection.serial.lastPort</c>),
    /// read at the start of each round.</param>
    /// <param name="open">Makes the connection for one port (a <see cref="SerialConnection"/>).</param>
    public AutoSerialConnection(ISerialPortInfo ports, bool haveInfo, Func<string?> lastPort,
                                Func<string, IControllerConnection> open,
                                AutoSerialOptions? options = null, TimeProvider? time = null)
    {
        _ports = ports;
        _haveInfo = haveInfo;
        _lastPort = lastPort;
        _open = open;
        _options = options ?? AutoSerialOptions.Default;
        _time = time ?? TimeProvider.System;
    }

    public event EventHandler<ControllerMessage>? MessageReceived;
    public event EventHandler<TransportStatus>? StatusChanged;

    /// <summary>Raised with <see cref="IsProbing"/> each time it changes.</summary>
    public event EventHandler<bool>? ProbingChanged;

    /// <summary>Raised once per lock-in, with the port whose Hello arrived.</summary>
    public event EventHandler<string>? PortFound;

    public TransportStatus Status { get; private set; } = TransportStatus.Closed;

    /// <summary>The locked port, else the best candidate a round with no answer settled on, else
    /// null (probing, or no best candidate).</summary>
    public string? CurrentPort
    {
        get
        {
            lock (_gate)
                return _mode switch
                {
                    Mode.Locked => _currentName,
                    Mode.Settled => _settledName,
                    _ => null,
                };
        }
    }

    /// <summary>Whether a round is trying the ports now.</summary>
    public bool IsProbing
    {
        get { lock (_gate) return _mode == Mode.Probing; }
    }

    public void Connect()
    {
        lock (_gate)
        {
            if (_disposed || _started) return;
            _started = true;
            _pollTimer = _time.CreateTimer(PollTick, null, _options.PortListPoll, _options.PortListPoll);
            StartRoundLocked();
        }
        Flush();
    }

    public void Disconnect()
    {
        lock (_gate)
        {
            if (_disposed) return;
            StopLocked();
        }
        Flush();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            StopLocked();
            _disposed = true;
        }
        Flush();
    }

    public void Send(byte[] data)
    {
        IControllerConnection? current;
        lock (_gate) current = _current;
        current?.Send(data);
    }

    /// <summary>Reads the port list and tries its candidates from the first.</summary>
    private void StartRoundLocked()
    {
        RetireLocked();
        _settledName = null;
        _listed = ListPorts();
        _candidates = SerialPortOrder.Candidates(_listed, _lastPort(), _haveInfo);
        _next = 0;
        if (_candidates.Count == 0)
        {
            SetModeLocked(Mode.Idle);
            SetStatusLocked(TransportStatus.Closed);
            return;
        }
        SetStatusLocked(TransportStatus.Open);
        SetModeLocked(Mode.Probing);
        TryNextLocked();
    }

    /// <summary>Opens the next candidate that opens and starts its wait, or ends the round.</summary>
    private void TryNextLocked()
    {
        while (_next < _candidates.Count)
        {
            var connection = OpenLocked(_candidates[_next++].Name);
            if (connection is null) continue;
            _perPortTimer = _time.CreateTimer(PerPortElapsed, connection, _options.PerPort, Timeout.InfiniteTimeSpan);
            return;
        }
        EndRoundLocked();
    }

    /// <summary>No port answered: keep the best candidate open, or none.</summary>
    private void EndRoundLocked()
    {
        var best = SerialPortOrder.BestCandidate(_candidates);
        if (best is not null && _current is null) OpenLocked(best.Name);
        _settledName = best?.Name;
        SetModeLocked(Mode.Settled);
        AppLog.Info(best is null
            ? "Automatic port: no controller answered"
            : $"Automatic port: no controller answered, staying on {best.Name}");
    }

    /// <summary>Opens one port and makes it current, or closes it at once and returns null when it
    /// doesn't open (another app holds it, or it's gone).</summary>
    private IControllerConnection? OpenLocked(string name)
    {
        IControllerConnection connection;
        try { connection = _open(name); }
        catch (Exception ex)
        {
            AppLog.Error($"Automatic port: can't open {name}: {ex.Message}");
            return null;
        }

        connection.MessageReceived += OnInnerMessage;
        connection.StatusChanged += OnInnerStatus;
        try { connection.Connect(); }
        catch (Exception ex) { AppLog.Error($"Automatic port: can't open {name}: {ex.Message}"); }

        if (connection.Status != TransportStatus.Open)
        {
            Close(connection);
            return null;
        }
        _current = connection;
        _currentName = name;
        return connection;
    }

    private void PerPortElapsed(object? state)
    {
        lock (_gate)
        {
            if (_disposed || _mode != Mode.Probing || !ReferenceEquals(state, _current)) return;
            StopPerPortLocked();
            // The last try is the best candidate: stay on it rather than close and reopen it.
            if (_next >= _candidates.Count
                && SerialPortOrder.BestCandidate(_candidates)?.Name == _currentName)
            {
                EndRoundLocked();
                return;
            }
            RetireLocked();
            TryNextLocked();
        }
        Flush();
    }

    private void PollTick(object? state)
    {
        lock (_gate)
        {
            if (_disposed || !_started || _mode == Mode.Locked) return;
            var listed = ListPorts();
            if (listed.SequenceEqual(_listed)) return;
            AppLog.Info("Automatic port: the port list changed, trying the ports again");
            StartRoundLocked();
        }
        Flush();
    }

    private void OnInnerMessage(object? sender, ControllerMessage message)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _current)) return;
            if (message is HelloMessage && _mode != Mode.Locked)
            {
                StopPerPortLocked();
                SetModeLocked(Mode.Locked);
                var name = _currentName!;
                AppLog.Info($"Automatic port: the controller answered on {name}");
                _pending.Add(() => PortFound?.Invoke(this, name));
            }
        }
        Flush();
        MessageReceived?.Invoke(this, message);
    }

    private void OnInnerStatus(object? sender, TransportStatus status)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _current)) return;
            if (_mode == Mode.Locked && status == TransportStatus.Closed)
            {
                AppLog.Info($"Automatic port: {_currentName} went away, trying the ports again");
                StartRoundLocked();
            }
            else if (_mode == Mode.Probing && status != TransportStatus.Open)
            {
                RetireLocked();
                TryNextLocked();
            }
        }
        Flush();
    }

    private void StopLocked()
    {
        _started = false;
        _pollTimer?.Dispose();
        _pollTimer = null;
        RetireLocked();
        _candidates = [];
        _listed = [];
        _settledName = null;
        SetModeLocked(Mode.Idle);
        SetStatusLocked(TransportStatus.Closed);
    }

    /// <summary>Stops the wait and closes the current port, if any.</summary>
    private void RetireLocked()
    {
        StopPerPortLocked();
        var current = _current;
        _current = null;
        _currentName = null;
        if (current is not null) Close(current);
    }

    private void Close(IControllerConnection connection)
    {
        connection.MessageReceived -= OnInnerMessage;
        connection.StatusChanged -= OnInnerStatus;
        try { connection.Dispose(); } catch { /* already gone */ }
    }

    private void StopPerPortLocked()
    {
        _perPortTimer?.Dispose();
        _perPortTimer = null;
    }

    private IReadOnlyList<SerialPortDescription> ListPorts()
    {
        try { return _ports.List(); }
        catch { return []; }
    }

    private void SetModeLocked(Mode mode)
    {
        var wasProbing = _mode == Mode.Probing;
        _mode = mode;
        var probing = mode == Mode.Probing;
        if (wasProbing != probing) _pending.Add(() => ProbingChanged?.Invoke(this, probing));
    }

    private void SetStatusLocked(TransportStatus status)
    {
        if (Status == status) return;
        Status = status;
        _pending.Add(() => StatusChanged?.Invoke(this, status));
    }

    /// <summary>Raises the queued events, once the outermost caller has released the lock.</summary>
    private void Flush()
    {
        if (Monitor.IsEntered(_gate)) return;
        while (true)
        {
            Action[] pending;
            lock (_gate)
            {
                if (_pending.Count == 0) return;
                pending = _pending.ToArray();
                _pending.Clear();
            }
            foreach (var raise in pending) raise();
        }
    }
}
