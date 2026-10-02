using System.Diagnostics;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Runtime;
using Xunit;

namespace Volco.Core.Tests.Runtime;

public sealed class ControllerLinkTests : IDisposable
{
    private static readonly ControllerLinkOptions Fast = new(
        HelloRetryInterval: TimeSpan.FromMilliseconds(40),
        HeartbeatInterval: TimeSpan.FromMilliseconds(40),
        TickInterval: TimeSpan.FromMilliseconds(10));

    private static readonly ControllerLinkOptions SlowHeartbeat = new(
        TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(10));

    /// <summary>Never fires on its own within a test.</summary>
    private static readonly ControllerLinkOptions Quiet = new(
        TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.FromHours(1));

    private readonly FakeControllerConnection _conn = new(new ConnectionConfig());
    private readonly List<ButtonDisplay?> _displays = new()
    {
        new LedDisplay(0),
        new ScreenDisplay(ScreenContent.Text("LIVE", ScreenIcon.Mic, false, "Mic")),
        null,                                                    // indicator kind None
    };
    private ControllerLink? _link;

    private ControllerLink NewLink(ControllerLinkOptions? options = null)
    {
        _link = new ControllerLink(() => _displays.ToList(), options ?? Quiet);
        return _link;
    }

    public void Dispose() => _link?.Dispose();

    private static readonly HelloMessage GoodHello = new(ProtocolVersion.Current, 5,
        new[] { IndicatorKind.Led, IndicatorKind.Screen, IndicatorKind.None });

    private static bool Same(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);
    private int Count(byte[] frame) => _conn.SentFrames.Count(f => Same(f, frame));
    private static byte[] Screen1(ScreenTransition t = ScreenTransition.None, string top = "LIVE")
        => ProtocolEncoder.EncodeScreen(1, t, ScreenContent.Text(top, ScreenIcon.Mic, false, "Mic"));

    private static bool WaitUntil(Func<bool> condition, int ms = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition()) { if (sw.ElapsedMilliseconds > ms) return condition(); Thread.Sleep(5); }
        return true;
    }

    private ControllerLink Ready()
    {
        var link = NewLink();
        link.Attach(_conn);
        link.OnMessage(GoodHello);
        return link;
    }

    [Fact]
    public void Attach_SendsHelloRequest_AndNoStateUntilHello()
    {
        var link = NewLink();
        var states = new List<ControllerState>();
        link.StateChanged += (_, s) => states.Add(s);

        link.Attach(_conn);
        link.Push();

        Assert.Equal(new[] { ProtocolEncoder.EncodeHelloRequest() }, _conn.SentFrames);
        Assert.Equal(ControllerState.AwaitingHello, link.State);
        Assert.Equal(new[] { ControllerState.AwaitingHello }, states);
    }

    [Fact]
    public void HelloRequest_RetriesUntilHello_ThenStops()
    {
        var link = NewLink(Fast);
        link.Attach(_conn);
        Assert.True(WaitUntil(() => Count(ProtocolEncoder.EncodeHelloRequest()) >= 3));

        link.OnMessage(GoodHello);
        var after = Count(ProtocolEncoder.EncodeHelloRequest());
        Thread.Sleep(150);
        Assert.Equal(after, Count(ProtocolEncoder.EncodeHelloRequest()));
    }

    [Fact]
    public void Hello_RaisesHelloReceivedBeforeThePush_AndPushesEveryButtonWithAKind()
    {
        var link = NewLink();
        var sentBeforeHandler = -1;
        link.HelloReceived += (_, _) => sentBeforeHandler = _conn.SentCount;
        link.Attach(_conn);

        link.OnMessage(GoodHello);

        Assert.Equal(1, sentBeforeHandler);                     // only the HelloRequest so far
        Assert.Equal(ControllerState.Ready, link.State);
        Assert.Equal(1, Count(ProtocolEncoder.EncodeLed(0, 0)));
        Assert.Equal(1, Count(Screen1()));
        Assert.Equal(3, _conn.SentCount);                        // HelloRequest, Led 0, Screen 1
    }

    [Fact]
    public void Hello_WrongVersion_IsMismatch_SendsNothing_NoHeartbeat()
    {
        var link = NewLink(Fast);
        var helloSeen = false;
        link.HelloReceived += (_, _) => helloSeen = true;
        link.Attach(_conn);

        link.OnMessage(new HelloMessage(99, 5, new[] { IndicatorKind.Led }));
        var after = _conn.SentCount;
        link.Push();
        Thread.Sleep(150);

        Assert.False(helloSeen);
        Assert.Equal(ControllerState.VersionMismatch, link.State);
        Assert.Equal(after, _conn.SentCount);
    }

    [Fact]
    public void Push_SendsOnlyWhatChanged()
    {
        var link = Ready();
        var baseline = _conn.SentCount;

        link.Push();
        Assert.Equal(baseline, _conn.SentCount);

        _displays[0] = new LedDisplay(1);
        link.Push();
        Assert.Equal(baseline + 1, _conn.SentCount);
        Assert.True(Same(ProtocolEncoder.EncodeLed(0, 1), _conn.SentFrames[^1]));
    }

    [Fact]
    public void PresetChange_Synchronized_IsBeginScreensLedsCommit_EvenWhenUnchanged()
    {
        var link = Ready();
        var baseline = _conn.SentCount;

        link.PushPresetChange(ScreenTransition.SlideLeft, synchronized: true);

        var burst = _conn.SentFrames.Skip(baseline).ToList();
        Assert.Equal(4, burst.Count);
        Assert.True(Same(ProtocolEncoder.EncodeBegin(), burst[0]));
        Assert.True(Same(Screen1(ScreenTransition.SlideLeft), burst[1]));
        Assert.True(Same(ProtocolEncoder.EncodeLed(0, 0), burst[2]));
        Assert.True(Same(ProtocolEncoder.EncodeCommit(), burst[3]));
    }

    [Fact]
    public void PresetChange_Unsynchronized_SameMessagesWithoutBeginCommit()
    {
        var link = Ready();
        var baseline = _conn.SentCount;

        link.PushPresetChange(ScreenTransition.SlideRight, synchronized: false);

        var burst = _conn.SentFrames.Skip(baseline).ToList();
        Assert.Equal(2, burst.Count);
        Assert.True(Same(Screen1(ScreenTransition.SlideRight), burst[0]));
        Assert.True(Same(ProtocolEncoder.EncodeLed(0, 0), burst[1]));
    }

    [Fact]
    public void PushAll_ResendsEverythingWithoutATransition()
    {
        var link = Ready();
        var baseline = _conn.SentCount;
        link.PushAll();
        Assert.Equal(baseline + 2, _conn.SentCount);
        Assert.Equal(2, Count(Screen1()));
    }

    [Fact]
    public void SecondHello_ClearsDedupAndPushesAgain()
    {
        var link = Ready();
        var baseline = _conn.SentCount;
        link.OnMessage(GoodHello);
        Assert.Equal(baseline + 2, _conn.SentCount);
    }

    [Fact]
    public void Heartbeat_OnlyWhenIdle()
    {
        var link = NewLink(SlowHeartbeat);
        link.Attach(_conn);
        link.OnMessage(GoodHello);
        Assert.True(WaitUntil(() => Count(ProtocolEncoder.EncodeHeartbeat()) >= 2, 5000));

        // Busy: a frame every 10 ms keeps the link from needing a heartbeat.
        var before = Count(ProtocolEncoder.EncodeHeartbeat());
        var sw = Stopwatch.StartNew();
        byte v = 0;
        while (sw.ElapsedMilliseconds < 400)
        {
            _displays[0] = new LedDisplay(v ^= 1);
            link.Push();
            Thread.Sleep(10);
        }
        Assert.InRange(Count(ProtocolEncoder.EncodeHeartbeat()) - before, 0, 1);
    }

    [Fact]
    public void Detach_StopsEverything()
    {
        var link = NewLink(Fast);
        link.Attach(_conn);
        link.OnMessage(GoodHello);
        link.Detach();
        var after = _conn.SentCount;
        Thread.Sleep(150);
        link.Push();
        Assert.Equal(after, _conn.SentCount);
        Assert.Equal(ControllerState.NoController, link.State);
    }

    [Fact]
    public void RapidPresetChanges_EachBurstIsCompleteAndInOrder()
    {
        // Review Focus #1: presses from several threads never interleave bursts.
        var link = Ready();
        var baseline = _conn.SentCount;
        var threads = Enumerable.Range(0, 6).Select(_ => new Thread(() =>
        {
            for (int i = 0; i < 20; i++) link.PushPresetChange(ScreenTransition.SlideLeft, synchronized: true);
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        var frames = _conn.SentFrames.Skip(baseline).ToList();
        Assert.Equal(120 * 4, frames.Count);
        for (int i = 0; i < frames.Count; i += 4)
        {
            Assert.True(Same(ProtocolEncoder.EncodeBegin(), frames[i]));
            Assert.True(Same(ProtocolEncoder.EncodeCommit(), frames[i + 3]));
        }
    }

    [Fact]
    public void ConcurrentPushes_NeverSendOlderStateAfterNewer()
    {
        int counter = 0;
        var linkWithCounter = new ControllerLink(
            () => new ButtonDisplay?[] { new LedDisplay((byte)Volatile.Read(ref counter)) }, Quiet);
        try
        {
            var conn = new FakeControllerConnection(new ConnectionConfig());
            linkWithCounter.Attach(conn);
            linkWithCounter.OnMessage(new HelloMessage(ProtocolVersion.Current, 0, new[] { IndicatorKind.Led }));
            var threads = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
            {
                for (int i = 0; i < 50; i++) { Interlocked.Increment(ref counter); linkWithCounter.Push(); }
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            var values = conn.SentFrames.Where(f => f[2] == (byte)MessageType.Led).Select(f => (int)f[4]).ToList();
            for (int i = 1; i < values.Count; i++) Assert.True(values[i] > values[i - 1], "an older value was sent after a newer one");
        }
        finally { linkWithCounter.Dispose(); }
    }

    [Fact]
    public void ComputeThrows_PushesDoNotThrow_SendNothing_AndALaterComputeStillSends()
    {
        var fail = true;
        var link = new ControllerLink(
            () => fail ? throw new InvalidOperationException("Collection was modified") : _displays.ToList(), Quiet);
        _link = link;
        link.Attach(_conn);

        link.OnMessage(GoodHello);                               // full push: compute throws
        Assert.Equal(ControllerState.Ready, link.State);
        link.Push();
        link.PushAll();
        link.PushPresetChange(ScreenTransition.SlideLeft, synchronized: true);
        Assert.Equal(new[] { ProtocolEncoder.EncodeHelloRequest() }, _conn.SentFrames);   // no Begin left open

        fail = false;
        link.Push();                                             // _sent untouched: everything differs
        Assert.Equal(1, Count(ProtocolEncoder.EncodeLed(0, 0)));
        Assert.Equal(1, Count(Screen1()));
    }

    private sealed class ScriptedConnection : IControllerConnection
    {
        private readonly object _lock = new();
        private readonly List<byte[]> _sent = new();
        private int _attempts;
        public Action<byte[]>? OnSend { get; set; }
        public int Attempts => Volatile.Read(ref _attempts);
        public int SentCount { get { lock (_lock) return _sent.Count; } }
        public event EventHandler<ControllerMessage>? MessageReceived { add { } remove { } }
        public event EventHandler<ConnectionStatus>? StatusChanged { add { } remove { } }
        public ConnectionStatus Status => ConnectionStatus.Connected;
        public void Connect() { }
        public void Disconnect() { }
        public void Dispose() { }
        public void Send(byte[] data)
        {
            Interlocked.Increment(ref _attempts);
            OnSend?.Invoke(data);
            lock (_lock) _sent.Add(data);
        }
    }

    [Fact]
    public void Detach_FromInsideSend_DoesNotThrow_AndSendsNothingMore()
    {
        var link = NewLink();
        var conn = new ScriptedConnection();
        link.Attach(conn);
        var afterAttach = conn.SentCount;
        conn.OnSend = _ => link.Detach();

        link.OnMessage(GoodHello);

        Assert.Equal(ControllerState.NoController, link.State);
        Assert.Equal(afterAttach + 1, conn.SentCount);   // the first frame only
    }

    [Fact]
    public void Send_ThatThrows_DoesNotCrashPushOrHeartbeat_AndLaterSendsWork()
    {
        var link = NewLink(Fast);
        var conn = new ScriptedConnection();
        link.Attach(conn);
        link.OnMessage(GoodHello);
        var throwing = true;
        conn.OnSend = _ => { if (Volatile.Read(ref throwing)) throw new IOException("boom"); };

        _displays[0] = new LedDisplay(1);
        link.Push();                                      // must not throw
        var attempts = conn.Attempts;
        Assert.True(WaitUntil(() => conn.Attempts >= attempts + 2));   // heartbeats keep trying

        Volatile.Write(ref throwing, false);
        var sent = conn.SentCount;
        _displays[0] = new LedDisplay(0);
        link.Push();
        Assert.True(WaitUntil(() => conn.SentCount > sent));
    }

    [Fact]
    public void Push_AfterDispose_SendsNothing_AndStateIsNoController()
    {
        var link = Ready();
        link.Dispose();
        var after = _conn.SentCount;

        _displays[0] = new LedDisplay(1);
        link.Push();
        link.PushAll();
        link.PushPresetChange(ScreenTransition.SlideLeft, true);
        link.Detach();

        Assert.Equal(after, _conn.SentCount);
        Assert.Equal(ControllerState.NoController, link.State);
    }

    [Fact]
    public void Hello_WithNoAttachment_DoesNotRaiseHelloReceived()
    {
        var link = NewLink();
        var seen = false;
        link.HelloReceived += (_, _) => seen = true;

        link.OnMessage(GoodHello);

        Assert.False(seen);
        Assert.Equal(ControllerState.NoController, link.State);
    }
}
