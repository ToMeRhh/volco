using Volco.Core.Diagnostics;

namespace Volco.Core.Communication;

/// <summary>How long Automatic waits on each port, and how often it looks at the port list.</summary>
/// <param name="PerPort">How long a port has to send its Hello: long enough for an ESP32 that the
/// open resets to boot and say it.</param>
/// <param name="PortListPoll">How often the port list is read while no port has answered (and a
/// busy best candidate is tried again).</param>
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
/// passes (closed, next). A round with no answer closes every port and settles on the best
/// candidate by name only (<see cref="CurrentPort"/>; nothing is open). Each port is tried once per
/// change of the port list, and again when the locked port goes, never in an endless cycle; a new
/// connection (Reconnect, a firmware action) starts a new round.
///
/// One exception: when the best candidate didn't open (another app holds it, e.g. a serial
/// monitor), the round ends busy: <see cref="Status"/> is <see cref="TransportStatus.Failed"/>
/// with <see cref="CurrentPort"/> naming it, and that one port is tried again on each port-list
/// poll until it opens; then it is probed as in a round (locked on its Hello, else closed and
/// settled, not tried again).
///
/// Otherwise <see cref="Status"/> is <see cref="TransportStatus.Closed"/> only with no candidates,
/// else <see cref="TransportStatus.Open"/>: the runtime's HelloRequests and timeouts carry on
/// across the ports, and a settled round reads as no answer.
///
/// Threading: a <see cref="SerialConnection"/> raises StatusChanged while holding its own lock and
/// takes that lock in Connect and Dispose, so this class never calls into an inner connection, the
/// port list or the factory while holding <see cref="_gate"/>, and never waits on anything inside an
/// inner callback. Every change of the round is a step on one serialised queue (<see cref="Post"/>),
/// run by whichever thread posts to an idle queue; an inner StatusChanged posts from the thread pool,
/// so it is never run on the inner connection's thread under its lock.
/// </summary>
public sealed class AutoSerialConnection : IControllerConnection
{
    private enum Mode { Idle, Probing, Settled, Busy, Locked }

    private readonly ISerialPortInfo _ports;
    private readonly bool _haveInfo;
    private readonly Func<string?> _lastPort;
    private readonly Func<string, IControllerConnection> _open;
    private readonly AutoSerialOptions _options;
    private readonly TimeProvider _time;

    // The steps, run one at a time, in order. _draining is true while a thread runs them.
    private readonly object _queueGate = new();
    private readonly Queue<Action> _steps = new();
    private bool _draining;
    private int _drainThread;

    // Guards the state below, only ever for reads and writes: no call out is made while it is held.
    // The steps are its only writers, apart from a Hello's lock-in (OnInnerMessage), which changes
    // only the mode and the wait.
    private readonly object _gate = new();
    private IReadOnlyList<SerialPortDescription> _listed = [];
    private IReadOnlyList<SerialPortDescription> _candidates = [];
    private int _next;
    private IControllerConnection? _current;
    private string? _currentName;
    private string? _settledName;
    private bool _bestBusy;            // the best candidate didn't open in this round
    private Mode _mode = Mode.Idle;
    private ITimer? _perPortTimer;
    private ITimer? _pollTimer;
    private bool _started;
    private bool _disposed;

    // Events queued under _gate and raised in order by one thread at a time (Flush).
    private readonly List<Action> _pending = new();
    private bool _flushing;

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

    /// <summary>The locked port, else the best candidate a round with no answer settled on (closed),
    /// or the busy one it waits for, else null (probing, or no best candidate).</summary>
    public string? CurrentPort
    {
        get
        {
            lock (_gate)
                return _mode switch
                {
                    Mode.Locked => _currentName,
                    Mode.Settled or Mode.Busy => _settledName,
                    _ => null,
                };
        }
    }

    /// <summary>Whether a round is trying the ports now.</summary>
    public bool IsProbing
    {
        get { lock (_gate) return _mode == Mode.Probing; }
    }

    /// <summary>Test seam: whether a step is queued or running.</summary>
    internal bool IsBusy
    {
        get { lock (_queueGate) return _draining || _steps.Count > 0; }
    }

    public void Connect() => Post(StartStep);

    /// <summary>Closes the port and stops trying; returns once the port is closed.</summary>
    public void Disconnect() => Stop(dispose: false);

    /// <summary>Closes the port for good; returns once the port is closed (a firmware flash opens
    /// it right after).</summary>
    public void Dispose() => Stop(dispose: true);

    /// <summary>Runs <see cref="StopStep"/> and waits for it when another thread is running the
    /// queue (it never waits on this caller). A call from a handler that the queue's own thread
    /// raised can't wait for itself: its stop runs as soon as that handler returns.</summary>
    private void Stop(bool dispose)
    {
        var done = new ManualResetEventSlim();
        Post(() =>
        {
            try { StopStep(dispose); }
            finally { done.Set(); }
        });
        if (!done.IsSet && Volatile.Read(ref _drainThread) == Environment.CurrentManagedThreadId)
            return;   // the step still sets it later, so it isn't disposed here
        done.Wait();
        done.Dispose();
    }

    public void Send(byte[] data)
    {
        IControllerConnection? current;
        lock (_gate) current = _current;
        current?.Send(data);
    }

    // ---- The queue ----

    /// <summary>Queues a step, and runs the queue on this thread if no other thread is.</summary>
    private void Post(Action step)
    {
        lock (_queueGate)
        {
            _steps.Enqueue(step);
            if (_draining) return;
            _draining = true;
            Volatile.Write(ref _drainThread, Environment.CurrentManagedThreadId);
        }
        while (true)
        {
            Action next;
            lock (_queueGate)
            {
                if (_steps.Count == 0)
                {
                    _draining = false;
                    Volatile.Write(ref _drainThread, 0);
                    break;
                }
                next = _steps.Dequeue();
            }
            try { next(); }
            catch (Exception ex) { AppLog.Error($"Automatic port: {ex.Message}"); }
            Flush();
        }
    }

    // ---- Steps (run by Post only, one at a time) ----

    private void StartStep()
    {
        lock (_gate)
        {
            if (_disposed || _started) return;
            _started = true;
        }
        var poll = _time.CreateTimer(_ => Post(PollStep), null, _options.PortListPoll, _options.PortListPoll);
        lock (_gate) _pollTimer = poll;
        StartRound(ListPorts());
    }

    private void StopStep(bool dispose)
    {
        ITimer? poll, perPort;
        IControllerConnection? current;
        lock (_gate)
        {
            if (_disposed) return;
            if (dispose) _disposed = true;
            _started = false;
            poll = _pollTimer;
            _pollTimer = null;
            (perPort, current) = TakeCurrentLocked();
            _candidates = [];
            _listed = [];
            _settledName = null;
            _bestBusy = false;
            SetModeLocked(Mode.Idle);
            SetStatusLocked(TransportStatus.Closed);
        }
        poll?.Dispose();
        Retire(perPort, current);
    }

    /// <summary>Tries the candidates of <paramref name="listed"/> from the first.</summary>
    private void StartRound(IReadOnlyList<SerialPortDescription> listed)
    {
        var candidates = SerialPortOrder.Candidates(listed, _lastPort(), _haveInfo);
        ITimer? perPort;
        IControllerConnection? current;
        lock (_gate)
        {
            if (!_started) return;
            (perPort, current) = TakeCurrentLocked();
            _settledName = null;
            _bestBusy = false;
            _listed = listed;
            _candidates = candidates;
            _next = 0;
            if (candidates.Count == 0)
            {
                SetModeLocked(Mode.Idle);
                SetStatusLocked(TransportStatus.Closed);
            }
            else
            {
                SetStatusLocked(TransportStatus.Open);
                SetModeLocked(Mode.Probing);
            }
        }
        Retire(perPort, current);
        if (candidates.Count > 0) TryNext();
    }

    /// <summary>Opens the next candidate that opens and starts its wait, or ends the round.</summary>
    private void TryNext()
    {
        while (true)
        {
            string name;
            lock (_gate)
            {
                if (!_started || _mode != Mode.Probing) return;
                if (_next >= _candidates.Count) break;
                name = _candidates[_next++].Name;
            }
            var connection = OpenPort(name);
            if (connection is null)
            {
                lock (_gate)
                    if (SerialPortOrder.BestCandidate(_candidates)?.Name == name) _bestBusy = true;
                continue;
            }
            var timer = _time.CreateTimer(_ => Post(() => PerPortStep(connection)), null,
                                          _options.PerPort, Timeout.InfiniteTimeSpan);
            bool stale;
            lock (_gate)
            {
                stale = !_started;
                if (!stale)
                {
                    _current = connection;
                    _currentName = name;
                    _perPortTimer = timer;
                }
            }
            if (stale) Retire(timer, connection);
            return;
        }
        EndRound();
    }

    /// <summary>No port answered: every port is closed already. Settles on the best candidate by
    /// name, or waits for it when it was busy (<see cref="Mode.Busy"/>).</summary>
    private void EndRound()
    {
        SerialPortDescription? best;
        bool busy;
        lock (_gate)
        {
            if (!_started) return;
            best = SerialPortOrder.BestCandidate(_candidates);
            busy = best is not null && _bestBusy;
            _settledName = best?.Name;
            SetModeLocked(busy ? Mode.Busy : Mode.Settled);
            SetStatusLocked(busy ? TransportStatus.Failed : TransportStatus.Open);
        }
        AppLog.Info(best is null
            ? "Automatic port: no controller answered"
            : busy
                ? $"Automatic port: {best.Name} is in use by another app, trying it again"
                : $"Automatic port: no controller answered, closed every port (best candidate {best.Name})");
    }

    /// <summary>The busy best candidate, tried again on a poll: once it opens, it is probed (it is
    /// the round's last try, so no answer settles).</summary>
    private void RetryBusy()
    {
        string name;
        lock (_gate)
        {
            if (!_started || _mode != Mode.Busy || _settledName is null) return;
            name = _settledName;
        }
        var connection = OpenPort(name);
        if (connection is null) return;
        var timer = _time.CreateTimer(_ => Post(() => PerPortStep(connection)), null,
                                      _options.PerPort, Timeout.InfiniteTimeSpan);
        bool stale;
        lock (_gate)
        {
            stale = !_started || _mode != Mode.Busy;
            if (!stale)
            {
                AppLog.Info($"Automatic port: {name} is free now, trying it");
                _current = connection;
                _currentName = name;
                _perPortTimer = timer;
                _bestBusy = false;
                _next = _candidates.Count;
                SetModeLocked(Mode.Probing);
                SetStatusLocked(TransportStatus.Open);
            }
        }
        if (stale) Retire(timer, connection);
    }

    private void PerPortStep(IControllerConnection connection)
    {
        ITimer? perPort;
        IControllerConnection? current;
        lock (_gate)
        {
            if (_mode != Mode.Probing || !ReferenceEquals(connection, _current)) return;
            // Spec §10.2: a port that doesn't answer is closed at once, the best candidate too.
            (perPort, current) = TakeCurrentLocked();
        }
        Retire(perPort, current);
        TryNext();
    }

    private void PollStep()
    {
        IReadOnlyList<SerialPortDescription> listed;
        lock (_gate)
        {
            if (!_started || _mode == Mode.Locked) return;
            listed = _listed;
        }
        var now = ListPorts();
        if (now.SequenceEqual(listed))
        {
            RetryBusy();
            return;
        }
        AppLog.Info("Automatic port: the port list changed, trying the ports again");
        StartRound(now);
    }

    private void InnerStatusStep(IControllerConnection connection, TransportStatus status)
    {
        Mode mode;
        string? name;
        lock (_gate)
        {
            if (!ReferenceEquals(connection, _current)) return;
            mode = _mode;
            name = _currentName;
        }
        if (mode == Mode.Locked && status == TransportStatus.Closed)
        {
            AppLog.Info($"Automatic port: {name} went away, trying the ports again");
            StartRound(ListPorts());
        }
        else if (mode == Mode.Probing && status != TransportStatus.Open)
        {
            ITimer? perPort;
            IControllerConnection? current;
            lock (_gate)
            {
                // A Hello may have locked it in since the first look.
                if (!ReferenceEquals(connection, _current) || _mode != Mode.Probing) return;
                (perPort, current) = TakeCurrentLocked();
            }
            Retire(perPort, current);
            TryNext();
        }
    }

    // ---- Inner callbacks (any thread, maybe under the inner connection's lock) ----

    private void OnInnerMessage(object? sender, ControllerMessage message)
    {
        ITimer? perPort = null;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _current)) return;
            if (message is HelloMessage && _mode != Mode.Locked)
            {
                perPort = _perPortTimer;
                _perPortTimer = null;
                SetModeLocked(Mode.Locked);
                var name = _currentName!;
                AppLog.Info($"Automatic port: the controller answered on {name}");
                _pending.Add(() => PortFound?.Invoke(this, name));
            }
        }
        perPort?.Dispose();
        Flush();
        MessageReceived?.Invoke(this, message);
    }

    private void OnInnerStatus(object? sender, TransportStatus status)
    {
        // Open needs nothing: a probed port is open already, and a locked one stays.
        if (sender is not IControllerConnection connection || status == TransportStatus.Open) return;
        // Off this thread: it may hold the inner connection's lock, and the step may dispose it.
        ThreadPool.QueueUserWorkItem(_ => Post(() => InnerStatusStep(connection, status)));
    }

    // ---- Helpers ----

    /// <summary>Opens one port, or closes it at once and returns null when it doesn't open
    /// (another app holds it, or it's gone). Called without <see cref="_gate"/>.</summary>
    private IControllerConnection? OpenPort(string name)
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

        if (connection.Status == TransportStatus.Open) return connection;
        Retire(null, connection);
        return null;
    }

    /// <summary>Detaches the current port and its wait, for <see cref="Retire"/> after the lock.</summary>
    private (ITimer? PerPort, IControllerConnection? Current) TakeCurrentLocked()
    {
        var taken = (_perPortTimer, _current);
        _perPortTimer = null;
        _current = null;
        _currentName = null;
        return taken;
    }

    /// <summary>Stops a wait and closes a port. Called without <see cref="_gate"/>.</summary>
    private void Retire(ITimer? perPort, IControllerConnection? connection)
    {
        perPort?.Dispose();
        if (connection is null) return;
        connection.MessageReceived -= OnInnerMessage;
        connection.StatusChanged -= OnInnerStatus;
        try { connection.Dispose(); } catch { /* already gone */ }
    }

    private IReadOnlyList<SerialPortDescription> ListPorts()
    {
        try { return _ports.List(); }
        catch (Exception ex)
        {
            AppLog.Error($"Automatic port: can't list the serial ports: {ex.Message}");
            return [];
        }
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

    /// <summary>Raises the queued events in order, on one thread at a time: a thread that finds
    /// another one flushing leaves its events to it.</summary>
    private void Flush()
    {
        lock (_gate)
        {
            if (_flushing) return;
            _flushing = true;
        }
        while (true)
        {
            Action[] pending;
            lock (_gate)
            {
                if (_pending.Count == 0) { _flushing = false; return; }
                pending = _pending.ToArray();
                _pending.Clear();
            }
            foreach (var raise in pending)
            {
                try { raise(); }
                catch (Exception ex) { AppLog.Error($"Automatic port: an event handler failed: {ex.Message}"); }
            }
        }
    }
}
