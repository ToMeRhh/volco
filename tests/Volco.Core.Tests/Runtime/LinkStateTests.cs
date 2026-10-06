using Microsoft.Extensions.Time.Testing;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Runtime;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.Core.Tests.Runtime;

/// <summary>The link state (spec 2026-10-06): "Connected" needs a Hello with this app's protocol
/// version, and a gap Volco expects is a wait, never a failure.</summary>
public sealed class LinkStateTests : IDisposable
{
    private static readonly HelloMessage Hello = new(ProtocolVersion.Current, 2, new[] { IndicatorKind.Led });

    private readonly string _dir;
    private readonly ConfigManager _config;
    private readonly FakeTimeProvider _time = new();
    private readonly List<FakeControllerConnection> _connections = new();
    private readonly List<LinkStatus> _changes = new();
    private VolcoRuntime? _runtime;

    public LinkStateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "volco-link-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigManager(Path.Combine(_dir, "config.json"));
        _config.Load();
        _config.Config.Connection.Serial.ComPort = "COM3";
    }

    public void Dispose()
    {
        _runtime?.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private VolcoRuntime Start(TimeSpan? grace = null)
    {
        _runtime = new VolcoRuntime(_config, new FakeAudioEngine(), true, cfg =>
        {
            var c = new FakeControllerConnection(cfg);
            lock (_connections) _connections.Add(c);
            return c;
        }, new VolcoRuntimeOptions
        {
            Link = new ControllerLinkOptions(TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.FromHours(1)),
            Time = _time,
            ConnectGrace = grace ?? TimeSpan.Zero,
        });
        _runtime.LinkStatusChanged += (_, s) => { lock (_changes) _changes.Add(s); };
        _runtime.Start();
        return _runtime;
    }

    private FakeControllerConnection Last { get { lock (_connections) return _connections[^1]; } }

    private List<LinkStatus> Changes { get { lock (_changes) return _changes.ToList(); } }

    // ── The pure function ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(TransportStatus.Closed, ControllerState.NoController, false, LinkState.Searching)]
    [InlineData(TransportStatus.Failed, ControllerState.NoController, false, LinkState.PortBusy)]
    [InlineData(TransportStatus.Open, ControllerState.AwaitingHello, false, LinkState.Waiting)]
    [InlineData(TransportStatus.Open, ControllerState.AwaitingHello, true, LinkState.NoAnswer)]
    [InlineData(TransportStatus.Open, ControllerState.VersionMismatch, false, LinkState.Incompatible)]
    [InlineData(TransportStatus.Open, ControllerState.Ready, false, LinkState.Connected)]
    public void TheTransportAndTheSession_GiveTheLinkState(TransportStatus transport, ControllerState controller, bool silent,
                                                           LinkState expected)
        => Assert.Equal(expected, LinkStateLogic.Compute(new(false, transport, controller, silent, LinkActivity.None, false)).State);

    [Fact]
    public void AnOpenPort_IsNeverConnected_WithoutAHello()
    {
        var link = LinkStateLogic.Compute(new(false, TransportStatus.Open, ControllerState.AwaitingHello, false, LinkActivity.None, false));
        Assert.False(link.IsConnected);
        Assert.Equal(LinkActivity.Connecting, link.Activity);
        Assert.False(link.IsDown);   // a wait, not a failure
    }

    [Fact]
    public void AnInterruption_HidesTheTransport_ButNotAnAnswer()
    {
        LinkStatus With(TransportStatus t, ControllerState c) =>
            LinkStateLogic.Compute(new(false, t, c, true, LinkActivity.UpdatingFirmware, false));

        Assert.Equal(new LinkStatus(LinkState.Waiting, LinkActivity.UpdatingFirmware), With(TransportStatus.Closed, ControllerState.NoController));
        Assert.Equal(new LinkStatus(LinkState.Waiting, LinkActivity.UpdatingFirmware), With(TransportStatus.Open, ControllerState.AwaitingHello));
        Assert.Equal(LinkState.Connected, With(TransportStatus.Open, ControllerState.Ready).State);
        Assert.Equal(LinkState.Incompatible, With(TransportStatus.Open, ControllerState.VersionMismatch).State);
    }

    [Fact]
    public void ASave_IsARestart_UntilTheControllerAnswers()
    {
        Assert.Equal(new LinkStatus(LinkState.Waiting, LinkActivity.Restarting),
            LinkStateLogic.Compute(new(false, TransportStatus.Closed, ControllerState.NoController, false, LinkActivity.None, Saving: true)));
        Assert.Equal(LinkState.Connected,
            LinkStateLogic.Compute(new(false, TransportStatus.Open, ControllerState.Ready, false, LinkActivity.None, Saving: true)).State);
    }

    [Fact]
    public void TheUsersDisconnect_WinsOverEverything()
        => Assert.Equal(LinkStatus.Disconnected,
            LinkStateLogic.Compute(new(true, TransportStatus.Open, ControllerState.Ready, false, LinkActivity.Restarting, true)));

    [Theory]
    [InlineData(LinkState.Connected, false)]
    [InlineData(LinkState.Waiting, false)]
    [InlineData(LinkState.Searching, true)]
    [InlineData(LinkState.PortBusy, true)]
    [InlineData(LinkState.NoAnswer, true)]
    [InlineData(LinkState.Incompatible, true)]
    [InlineData(LinkState.Disconnected, true)]
    public void OnlyConnectedAndWaiting_AreNotDown(LinkState state, bool down)
        => Assert.Equal(down, new LinkStatus(state).IsDown);

    // ── The runtime ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheRuntime_FollowsTheTransportAndTheHello()
    {
        var runtime = Start();
        Assert.Equal(LinkState.Searching, runtime.LinkStatus.State);

        Last.SetStatus(TransportStatus.Open);
        Assert.Equal(new LinkStatus(LinkState.Waiting, LinkActivity.Connecting), runtime.LinkStatus);

        Last.RaiseMessage(Hello);
        Assert.Equal(LinkState.Connected, runtime.LinkStatus.State);
        Assert.Equal(LinkState.Connected, Changes[^1].State);

        Last.SetStatus(TransportStatus.Closed);   // unplugged: Volco didn't expect it
        Assert.Equal(LinkState.Searching, runtime.LinkStatus.State);
        Assert.True(runtime.LinkStatus.IsDown);
    }

    [Fact]
    public void AnotherProtocolVersion_IsIncompatible()
    {
        var runtime = Start();
        Last.SetStatus(TransportStatus.Open);
        Last.RaiseMessage(new HelloMessage(9, 2, new[] { IndicatorKind.Led }));
        Assert.Equal(LinkState.Incompatible, runtime.LinkStatus.State);
    }

    [Fact]
    public void ASilentPort_BecomesNoAnswer()
    {
        var runtime = Start();
        Last.SetStatus(TransportStatus.Open);
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(LinkState.NoAnswer, runtime.LinkStatus.State);
    }

    [Fact]
    public void TheConnectGrace_IsAWait_ThenWhatTheTransportSays()
    {
        var runtime = Start(grace: TimeSpan.FromSeconds(3));
        Assert.Equal(new LinkStatus(LinkState.Waiting, LinkActivity.Connecting), runtime.LinkStatus);   // the port isn't there yet
        Assert.Equal(new LinkStatus(LinkState.Waiting, LinkActivity.Connecting), Changes[0]);          // no red flash first
        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(LinkState.Searching, runtime.LinkStatus.State);

        lock (_changes) _changes.Clear();
        runtime.Reconnect();                                   // a Reconnect gets its own grace
        Assert.Equal(LinkState.Waiting, runtime.LinkStatus.State);
        Assert.All(Changes, c => Assert.Equal(LinkState.Waiting, c.State));
    }

    [Fact]
    public void AnExpectedInterruption_IsNeverDown_AndEndsInWhatIsThere()
    {
        var runtime = Start();
        Last.SetStatus(TransportStatus.Open);
        Last.RaiseMessage(Hello);
        lock (_changes) _changes.Clear();

        var expected = runtime.ExpectInterruption(LinkActivity.UpdatingFirmware);
        using (runtime.SuspendConnection())
            Assert.Equal(new LinkStatus(LinkState.Waiting, LinkActivity.UpdatingFirmware), runtime.LinkStatus);
        Last.SetStatus(TransportStatus.Open);                 // the resumed transport; the board is booting
        _time.Advance(TimeSpan.FromSeconds(6));               // past the silence: held back
        Assert.False(runtime.ControllerSilent);
        Last.RaiseMessage(Hello);
        expected.Dispose();

        Assert.Equal(LinkState.Connected, runtime.LinkStatus.State);
        Assert.All(Changes, c => Assert.False(c.IsDown, $"{c} during an expected interruption"));
    }

    [Fact]
    public void ASilenceHeldBack_CountsWhenTheInterruptionEnds()
    {
        var runtime = Start();
        var expected = runtime.ExpectInterruption(LinkActivity.InstallingFirmware);
        Last.SetStatus(TransportStatus.Open);
        _time.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(LinkState.Waiting, runtime.LinkStatus.State);

        expected.Dispose();
        Assert.True(runtime.ControllerSilent);
        Assert.Equal(LinkState.NoAnswer, runtime.LinkStatus.State);
        expected.Dispose();                                   // idempotent
    }

    [Fact]
    public void AnInterruptionWithALimit_EndsByItself()
    {
        var runtime = Start();
        runtime.ExpectInterruption(LinkActivity.Restarting, TimeSpan.FromSeconds(10));
        Assert.Equal(new LinkStatus(LinkState.Waiting, LinkActivity.Restarting), runtime.LinkStatus);
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(LinkState.Searching, runtime.LinkStatus.State);
    }

    [Fact]
    public void ASave_KeepsTheLinkWaiting_WhenTheTransportDropsForTheRestart()
    {
        _config.Config.Connection.Type = ConnectionType.Udp;
        var runtime = Start();
        Last.SetStatus(TransportStatus.Open);
        Last.RaiseMessage(Hello);

        _ = runtime.ControllerConfig.SaveAsync(new byte[] { 1, 2, 3 });
        Last.SetStatus(TransportStatus.Closed);               // the board reboots: no packets for a while
        Assert.Equal(new LinkStatus(LinkState.Waiting, LinkActivity.Restarting), runtime.LinkStatus);
        Assert.False(runtime.LinkStatus.IsDown);
    }

    [Fact]
    public void TheUsersDisconnect_IsTheOnlyDisconnected()
    {
        var runtime = Start();
        runtime.Disconnect();
        Assert.Equal(LinkStatus.Disconnected, runtime.LinkStatus);
        Assert.Equal(LinkState.Disconnected, Changes[^1].State);
        runtime.Connect();
        Assert.Equal(LinkState.Searching, runtime.LinkStatus.State);
    }
}
