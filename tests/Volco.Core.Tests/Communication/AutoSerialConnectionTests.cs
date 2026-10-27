using Microsoft.Extensions.Time.Testing;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Tests.Runtime;
using Xunit;

namespace Volco.Core.Tests.Communication;

/// <summary>Automatic port (spec §10.2): the ports are tried one at a time until one answers with
/// a Hello.</summary>
public sealed class AutoSerialConnectionTests : IDisposable
{
    private static readonly HelloMessage Hello = new(ProtocolVersion.Current, 2, new[] { IndicatorKind.Led });
    private static readonly TimeSpan PerPort = TimeSpan.FromSeconds(3);

    private readonly FakeTimeProvider _time = new();
    private readonly FakePorts _ports = new();
    private readonly List<(string Name, FakeControllerConnection Fake)> _opened = new();
    private readonly Dictionary<string, TransportStatus> _statusOnConnect = new();
    private readonly Dictionary<string, (ManualResetEventSlim Entered, ManualResetEventSlim Release)> _blockOpen = new();
    private readonly List<string> _found = new();
    private readonly List<bool> _probing = new();
    private readonly List<ControllerMessage> _received = new();
    private string? _lastPort;
    private bool _priorDisposedAtEachOpen = true;
    private AutoSerialConnection? _auto;

    public void Dispose() => _auto?.Dispose();

    private static SerialPortDescription Usb(string name) => new(name, "USB Serial Device", 0x10C4, 0xEA60);
    private static SerialPortDescription Ch340(string name) => new(name, "USB-SERIAL CH340", 0x1A86, 0x7523);
    private static SerialPortDescription Bluetooth(string name) => new(name, "Standard Serial over Bluetooth link");

    private AutoSerialConnection Start(params SerialPortDescription[] ports)
    {
        _ports.Ports = ports;
        _auto = new AutoSerialConnection(_ports, haveInfo: true, () => _lastPort, Open, AutoSerialOptions.Default, _time);
        _auto.PortFound += (_, name) => _found.Add(name);
        _auto.ProbingChanged += (_, probing) => _probing.Add(probing);
        _auto.MessageReceived += (_, msg) => _received.Add(msg);
        _auto.Connect();
        return _auto;
    }

    private IControllerConnection Open(string name)
    {
        if (_blockOpen.TryGetValue(name, out var block))
        {
            block.Entered.Set();
            block.Release.Wait(TimeSpan.FromSeconds(10));
        }
        if (_opened.Any(o => o.Fake.DisposeCount == 0)) _priorDisposedAtEachOpen = false;
        var fake = new FakeControllerConnection(new ConnectionConfig())
        {
            StatusOnConnect = _statusOnConnect.TryGetValue(name, out var s) ? s : TransportStatus.Open,
        };
        _opened.Add((name, fake));
        return fake;
    }

    private FakeControllerConnection Last(string name) => _opened.Last(o => o.Name == name).Fake;
    private IEnumerable<string> OpenedNames => _opened.Select(o => o.Name);

    [Fact]
    public void TriesCh340FirstAndLocksOnHello()
    {
        var auto = Start(Usb("COM1"), Ch340("COM7"));
        Assert.True(auto.IsProbing);

        Last("COM7").RaiseMessage(Hello);

        Assert.Equal("COM7", auto.CurrentPort);
        Assert.Equal(new[] { "COM7" }, _found);
        Assert.DoesNotContain("COM1", OpenedNames);
        Assert.False(auto.IsProbing);
        Assert.Equal(new[] { true, false }, _probing);
        Assert.Same(Hello, Assert.Single(_received));
        Assert.Equal(TransportStatus.Open, auto.Status);

        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(0, Last("COM7").DisposeCount);
        Assert.Single(_opened);
    }

    [Fact]
    public void MovesOnAfterPerPortWithoutHello()
    {
        var auto = Start(Usb("COM1"), Ch340("COM7"));
        Assert.Equal(new[] { "COM7" }, OpenedNames);

        _time.Advance(PerPort - TimeSpan.FromMilliseconds(1));
        Assert.Equal(new[] { "COM7" }, OpenedNames);
        _time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(new[] { "COM7", "COM1" }, OpenedNames);
        Assert.True(_priorDisposedAtEachOpen);
        Last("COM1").RaiseMessage(Hello);
        Assert.Equal("COM1", auto.CurrentPort);
        Assert.Equal(new[] { "COM1" }, _found);
    }

    [Fact]
    public void ForwardsSendsAndMessagesOfTheProbedPort()
    {
        var auto = Start(Ch340("COM7"));
        var alive = new AliveMessage();

        auto.Send(new byte[] { 1, 2 });
        Last("COM7").RaiseMessage(alive);

        Assert.Equal(new byte[] { 1, 2 }, Assert.Single(Last("COM7").SentFrames));
        Assert.Same(alive, Assert.Single(_received));
        Assert.Empty(_found);
    }

    [Fact]
    public void SkipsNonUsbAndBusyPorts()
    {
        _statusOnConnect["COM3"] = TransportStatus.Failed;
        var auto = Start(Bluetooth("COM2"), Usb("COM3"), Usb("COM4"));

        Assert.Equal(new[] { "COM3", "COM4" }, OpenedNames);
        Assert.Equal(1, Last("COM3").DisposeCount);
        Assert.Equal(0, Last("COM4").DisposeCount);
        Assert.True(auto.IsProbing);
    }

    [Fact]
    public void NoPortsIsClosed()
    {
        var auto = Start();

        Assert.Equal(TransportStatus.Closed, auto.Status);
        Assert.False(auto.IsProbing);
        Assert.Null(auto.CurrentPort);
        Assert.Empty(_opened);
    }

    [Fact]
    public void NoAnswerClosesEveryPort()
    {
        var auto = Start(Usb("COM1"), Ch340("COM7"));
        Assert.Null(auto.CurrentPort);

        _time.Advance(PerPort);
        _time.Advance(PerPort);

        // Spec §10.2: closed at once when it doesn't answer, the best candidate too; it is still
        // named, so the status reads "not responding" on it.
        Assert.False(auto.IsProbing);
        Assert.Equal("COM7", auto.CurrentPort);
        Assert.All(_opened, o => Assert.Equal(1, o.Fake.DisposeCount));
        Assert.Equal(TransportStatus.Open, auto.Status);
        Assert.Equal(new[] { "COM7", "COM1" }, OpenedNames);

        // Nothing is open, so nothing is sent; and nothing is tried again without a change.
        auto.Send(new byte[] { 1 });
        Assert.All(_opened, o => Assert.Empty(o.Fake.SentFrames));
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(2, _opened.Count);
        Assert.Empty(_found);
    }

    [Fact]
    public void BusyCh340IsFailedAndRetriedUntilItOpens()
    {
        _statusOnConnect["COM7"] = TransportStatus.Failed;   // a serial monitor holds it
        var statuses = new List<TransportStatus>();
        _ports.Ports = new[] { Ch340("COM7") };
        var auto = new AutoSerialConnection(_ports, haveInfo: true, () => null, Open, AutoSerialOptions.Default, _time);
        _auto = auto;
        auto.StatusChanged += (_, s) => statuses.Add(s);
        auto.PortFound += (_, name) => _found.Add(name);
        auto.Connect();

        Assert.Equal(TransportStatus.Failed, auto.Status);
        Assert.Equal("COM7", auto.CurrentPort);
        Assert.False(auto.IsProbing);
        Assert.Equal(1, Last("COM7").DisposeCount);

        // Still busy: tried again on each poll, and closed again.
        _time.Advance(AutoSerialOptions.Default.PortListPoll);
        Assert.Equal(2, _opened.Count);
        Assert.Equal(TransportStatus.Failed, auto.Status);

        // Freed: it opens and is probed as usual.
        _statusOnConnect.Remove("COM7");
        _time.Advance(AutoSerialOptions.Default.PortListPoll);
        Assert.Equal(3, _opened.Count);
        Assert.Equal(TransportStatus.Open, auto.Status);
        Assert.True(auto.IsProbing);
        Last("COM7").RaiseMessage(Hello);

        Assert.Equal("COM7", auto.CurrentPort);
        Assert.Equal(new[] { "COM7" }, _found);
        Assert.Equal(new[] { TransportStatus.Open, TransportStatus.Failed, TransportStatus.Open }, statuses);
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(3, _opened.Count);
    }

    [Fact]
    public void FreedBusyCandidateWithoutHelloSettlesAndIsNotRetried()
    {
        _statusOnConnect["COM7"] = TransportStatus.Failed;
        var auto = Start(Usb("COM1"), Ch340("COM7"));
        _time.Advance(PerPort);   // COM1 stays silent too
        Assert.Equal(TransportStatus.Failed, auto.Status);
        Assert.Equal("COM7", auto.CurrentPort);

        _statusOnConnect.Remove("COM7");
        _time.Advance(AutoSerialOptions.Default.PortListPoll);
        Assert.True(auto.IsProbing);
        _time.Advance(PerPort);

        Assert.False(auto.IsProbing);
        Assert.Equal(TransportStatus.Open, auto.Status);
        Assert.Equal("COM7", auto.CurrentPort);
        Assert.All(_opened, o => Assert.Equal(1, o.Fake.DisposeCount));
        var count = _opened.Count;
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(count, _opened.Count);
    }

    [Fact]
    public void BusyPortThatIsNotTheBestIsNotRetried()
    {
        _statusOnConnect["COM1"] = TransportStatus.Failed;
        var auto = Start(Usb("COM1"), Usb("COM2"));
        _time.Advance(PerPort);

        Assert.Equal(TransportStatus.Open, auto.Status);
        Assert.Null(auto.CurrentPort);
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(new[] { "COM1", "COM2" }, OpenedNames);
    }

    [Fact]
    public void NoAnswerSeveralWithoutCh340SettlesOnNone()
    {
        var auto = Start(Usb("COM1"), Usb("COM2"));

        _time.Advance(PerPort);
        _time.Advance(PerPort);

        Assert.False(auto.IsProbing);
        Assert.Null(auto.CurrentPort);
        Assert.Equal(TransportStatus.Open, auto.Status);
        Assert.All(_opened, o => Assert.Equal(1, o.Fake.DisposeCount));
        Assert.Equal(new[] { "COM1", "COM2" }, OpenedNames);

        // Tried once per change of the port list, never in an endless cycle.
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(2, _opened.Count);
    }

    [Fact]
    public void LastPortIsTriedAfterCh340()
    {
        _lastPort = "COM5";
        Start(Usb("COM1"), Usb("COM5"), Ch340("COM7"));

        _time.Advance(PerPort);
        _time.Advance(PerPort);

        Assert.Equal(new[] { "COM7", "COM5", "COM1" }, OpenedNames);
    }

    [Fact]
    public void NewRoundOnPortListChange()
    {
        var auto = Start(Usb("COM1"), Usb("COM2"));
        _time.Advance(PerPort);
        _time.Advance(PerPort);
        Assert.Null(auto.CurrentPort);

        _ports.Ports = new[] { Usb("COM1"), Usb("COM2"), Ch340("COM9") };
        _time.Advance(AutoSerialOptions.Default.PortListPoll);

        Assert.Equal("COM9", _opened[^1].Name);
        Assert.True(auto.IsProbing);
        Last("COM9").RaiseMessage(Hello);
        Assert.Equal("COM9", auto.CurrentPort);
        Assert.Equal(new[] { "COM9" }, _found);
    }

    [Fact]
    public void ReprobesWhenLockedPortGoes()
    {
        var auto = Start(Usb("COM3"));
        Last("COM3").RaiseMessage(Hello);
        Assert.Equal("COM3", auto.CurrentPort);

        _ports.Ports = new[] { Ch340("COM4") };
        Last("COM3").SetStatus(TransportStatus.Closed);

        // An inner connection's status is handled off its own thread (it may hold its lock).
        Assert.True(SpinWait.SpinUntil(() => _opened.Count == 2 && !auto.IsBusy, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Last("COM3").DisposeCount);
        Assert.Equal("COM4", _opened[^1].Name);
        Assert.True(auto.IsProbing);
        Assert.Equal(TransportStatus.Open, auto.Status);
        Last("COM4").RaiseMessage(Hello);
        Assert.Equal("COM4", auto.CurrentPort);
        Assert.Equal(new[] { "COM3", "COM4" }, _found);
    }

    [Fact]
    public void DropsFramesFromRetiredPort()
    {
        Start(Usb("COM1"), Ch340("COM7"));
        var retired = Last("COM7");
        _time.Advance(PerPort);

        retired.RaiseMessage(Hello);
        retired.SetStatus(TransportStatus.Closed);
        Assert.True(SpinWait.SpinUntil(() => !_auto!.IsBusy, TimeSpan.FromSeconds(5)));

        Assert.Empty(_received);
        Assert.Empty(_found);
        Assert.Equal("COM1", _opened[^1].Name);
    }

    [Fact]
    public void DisconnectStopsTimersAndDisposesTheInnerConnection()
    {
        var auto = Start(Usb("COM1"), Ch340("COM7"));

        auto.Disconnect();
        _time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(1, Last("COM7").DisposeCount);
        Assert.Single(_opened);
        Assert.Equal(TransportStatus.Closed, auto.Status);
        Assert.False(auto.IsProbing);
    }

    /// <summary>VolcoRuntime's SuspendConnection hands the port to the flasher right after
    /// Dispose returns: Dispose waits for a step another thread is running, then frees the port.</summary>
    [Fact]
    public void DisposeWhileAnotherThreadIsMidStepReturnsAfterTheInnerDispose()
    {
        var block = (Entered: new ManualResetEventSlim(), Release: new ManualResetEventSlim());
        _blockOpen["COM1"] = block;
        var auto = Start(Usb("COM1"), Ch340("COM7"));

        // The per-port timer's thread: COM7 is closed and the open of COM1 is under way.
        var timer = new Thread(() => _time.Advance(PerPort)) { IsBackground = true };
        timer.Start();
        Assert.True(block.Entered.Wait(TimeSpan.FromSeconds(5)));

        int? com1DisposedAtReturn = null;
        var disposer = new Thread(() =>
        {
            auto.Dispose();
            var com1 = _opened.FirstOrDefault(o => o.Name == "COM1").Fake;
            com1DisposedAtReturn = com1?.DisposeCount ?? -1;
        }) { IsBackground = true };
        disposer.Start();
        Assert.False(disposer.Join(TimeSpan.FromMilliseconds(300)), "Dispose returned before the step ended");

        block.Release.Set();
        Assert.True(disposer.Join(TimeSpan.FromSeconds(5)));
        Assert.True(timer.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, com1DisposedAtReturn);
        Assert.Equal(TransportStatus.Closed, auto.Status);
    }

    /// <summary>An inner connection that raises StatusChanged while holding its own lock, and
    /// whose Dispose takes that lock, as <see cref="SerialConnection"/> does: Automatic must not
    /// hold its own lock while calling into it, nor take it inside its callbacks.</summary>
    [Fact]
    public void UnplugWhileRetiringDoesNotDeadlock()
    {
        var fake = new LockingConnection();
        _ports.Ports = new[] { Usb("COM1") };
        var auto = new AutoSerialConnection(_ports, haveInfo: true, () => null, _ => fake,
            new AutoSerialOptions(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3)), _time);
        _auto = auto;
        auto.Connect();
        Assert.True(auto.IsProbing);

        // The controller's own thread: unplugged, it reports Closed under its lock. The raise is
        // already in flight (its handlers read) when the retire below unsubscribes and disposes.
        var unplug = new Thread(() =>
        {
            lock (fake.Gate)
            {
                var raise = fake.StatusHandlers;
                fake.Held.Set();
                fake.DisposeEntered.Wait(TimeSpan.FromSeconds(5));
                raise?.Invoke(fake, TransportStatus.Closed);
            }
        }) { IsBackground = true };
        // The poll's thread: the port list changed, so the probed port is retired.
        var poll = new Thread(() =>
        {
            fake.Held.Wait(TimeSpan.FromSeconds(5));
            _ports.Ports = [];
            _time.Advance(TimeSpan.FromSeconds(3));
        }) { IsBackground = true };
        unplug.Start();
        poll.Start();

        Assert.True(unplug.Join(TimeSpan.FromSeconds(5)), "the unplug callback deadlocked");
        Assert.True(poll.Join(TimeSpan.FromSeconds(5)), "the retire deadlocked");
        Assert.True(SpinWait.SpinUntil(() => !auto.IsBusy, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, fake.DisposeCount);
        Assert.Equal(TransportStatus.Closed, auto.Status);
    }

    private sealed class LockingConnection : IControllerConnection
    {
        public readonly object Gate = new();
        public readonly ManualResetEventSlim Held = new();
        public readonly ManualResetEventSlim DisposeEntered = new();
        private int _disposeCount;

        public event EventHandler<ControllerMessage>? MessageReceived;
        public event EventHandler<TransportStatus>? StatusChanged;
        public TransportStatus Status { get; private set; }
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public EventHandler<TransportStatus>? StatusHandlers => StatusChanged;

        public void Connect()
        {
            lock (Gate) RaiseStatus(TransportStatus.Open);
        }

        public void Disconnect() { }
        public void Send(byte[] data) { }

        public void Dispose()
        {
            DisposeEntered.Set();
            lock (Gate) Interlocked.Increment(ref _disposeCount);
        }

        public void RaiseStatus(TransportStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(this, status);
            _ = MessageReceived;
        }
    }

    private sealed class FakePorts : ISerialPortInfo
    {
        public IReadOnlyList<SerialPortDescription> Ports { get; set; } = [];
        public IReadOnlyList<SerialPortDescription> List() => Ports;
    }
}
