using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Runtime;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.Core.Tests.Runtime;

/// <summary>The firmware helpers through <see cref="VolcoRuntime"/> (issue #120): the pin watch and its
/// renewal, PinActivity, the I2C scan, PinDrive and the Try-the-lights steps.</summary>
public sealed class PinHelpersRuntimeTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(2);

    private static readonly HelloMessage Hello = new(ProtocolVersion.Current, 2,
        new[] { IndicatorKind.Led, IndicatorKind.LedPair });

    private readonly FakeTimeProvider _time = new();
    private readonly FakeAudioEngine _engine = new();
    private readonly List<FakeControllerConnection> _connections = new();   // guarded by itself
    private readonly List<VolcoRuntime> _runtimes = new();
    private readonly string _dir;

    public PinHelpersRuntimeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"volco-pin-helpers-{Guid.NewGuid()}");
        Directory.CreateDirectory(_dir);
        var speakers = new AudioDevice("spk", "Speakers", AudioFlow.Render, true);
        _engine.Devices = new List<AudioDevice> { speakers };
        _engine.DefaultRender = speakers;
        _engine.DeviceStates["spk"] = (0.5f, false);
    }

    public void Dispose()
    {
        foreach (var runtime in _runtimes) runtime.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private FakeControllerConnection Connection(int index) { lock (_connections) return _connections[index]; }

    /// <summary>A runtime on the fake clock, its controller connected and greeted.</summary>
    private VolcoRuntime StartConnected()
    {
        var config = new ConfigManager(Path.Combine(_dir, "config.json"));
        config.Load();
        var runtime = new VolcoRuntime(config, _engine, true, c =>
        {
            var connection = new FakeControllerConnection(c);
            lock (_connections) _connections.Add(connection);
            return connection;
        }, new VolcoRuntimeOptions
        {
            Link = new ControllerLinkOptions(TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.FromHours(1)),
            ConnectGrace = TimeSpan.Zero,
            Time = _time,
        });
        _runtimes.Add(runtime);
        runtime.Start();
        Connection(0).SetStatus(TransportStatus.Open);
        Connection(0).RaiseMessage(Hello);
        return runtime;
    }

    /// <summary>The frames of one type sent on <paramref name="c"/>, in order.</summary>
    private static List<byte[]> Frames(FakeControllerConnection c, MessageType type)
        => c.SentFrames.Where(f => f.Length > 2 && f[2] == (byte)type).ToList();

    private static readonly byte[] WatchAnalog = ProtocolEncoder.EncodePinWatch(PinWatchRole.Analog);
    private static readonly byte[] WatchButton = ProtocolEncoder.EncodePinWatch(PinWatchRole.Button);
    private static readonly byte[] WatchStop = ProtocolEncoder.EncodePinWatch(PinWatchRole.Stop);

    private static byte[] Drive(int pin, bool high, int ms) => ProtocolEncoder.EncodePinDrive(pin, high, (ushort)ms);

    private static async Task<T> Within<T>(Task<T> task) => await task.WaitAsync(WaitTimeout);

    // ── Pin watch ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StartPinWatch_SendsNowRenewsAndStopsOnDispose()
    {
        var runtime = StartConnected();
        var c = Connection(0);

        var watch = runtime.StartPinWatch(PinWatchRole.Analog);
        Assert.Equal(new[] { WatchAnalog }, Frames(c, MessageType.PinWatch));

        _time.Advance(TimeSpan.FromSeconds(9));
        Assert.Single(Frames(c, MessageType.PinWatch));
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new[] { WatchAnalog, WatchAnalog }, Frames(c, MessageType.PinWatch));

        watch.Dispose();
        Assert.Equal(new[] { WatchAnalog, WatchAnalog, WatchStop }, Frames(c, MessageType.PinWatch));

        _time.Advance(TimeSpan.FromSeconds(10));
        watch.Dispose();   // idempotent
        Assert.Equal(3, Frames(c, MessageType.PinWatch).Count);
    }

    [Fact]
    public void StartPinWatch_ResendsAfterHello()
    {
        var runtime = StartConnected();
        var c = Connection(0);
        using var watch = runtime.StartPinWatch(PinWatchRole.Button);

        c.RaiseMessage(Hello);   // e.g. the controller rebooted after a save
        Assert.Equal(new[] { WatchButton, WatchButton }, Frames(c, MessageType.PinWatch));

        c.RaiseMessage(HelloMessage.VersionOnly((byte)(ProtocolVersion.Current + 1)));   // not one this app speaks
        Assert.Equal(2, Frames(c, MessageType.PinWatch).Count);
    }

    [Fact]
    public void AHelloFromARetiredConnection_ResendsNothing()
    {
        var runtime = StartConnected();
        var old = Connection(0);
        using var watch = runtime.StartPinWatch(PinWatchRole.Analog);
        runtime.Reconnect();
        old.RaiseMessage(Hello);
        Assert.Single(Frames(old, MessageType.PinWatch));
    }

    [Fact]
    public void NewWatch_ReplacesOld_OldDisposeSendsNothing()
    {
        var runtime = StartConnected();
        var c = Connection(0);

        var first = runtime.StartPinWatch(PinWatchRole.Analog);
        var second = runtime.StartPinWatch(PinWatchRole.Button);
        first.Dispose();
        Assert.Equal(new[] { WatchAnalog, WatchButton }, Frames(c, MessageType.PinWatch));

        _time.Advance(TimeSpan.FromSeconds(10));   // only the current watch renews
        Assert.Equal(new[] { WatchAnalog, WatchButton, WatchButton }, Frames(c, MessageType.PinWatch));

        second.Dispose();
        Assert.Equal(WatchStop, Frames(c, MessageType.PinWatch)[^1]);
    }

    [Fact]
    public void Dispose_StopsTheWatch()
    {
        var runtime = StartConnected();
        var c = Connection(0);
        var watch = runtime.StartPinWatch(PinWatchRole.Analog);

        runtime.Dispose();
        Assert.Equal(new[] { WatchAnalog, WatchStop }, Frames(c, MessageType.PinWatch));

        _time.Advance(TimeSpan.FromSeconds(30));
        watch.Dispose();
        Assert.Equal(2, Frames(c, MessageType.PinWatch).Count);
    }

    [Fact]
    public void PinActivity_IsRaised()
    {
        var runtime = StartConnected();
        var c = Connection(0);
        var pins = new ConcurrentQueue<int>();
        runtime.PinActivity += (_, pin) => pins.Enqueue(pin);

        // The golden frame pin_activity_33, through the parser.
        var parser = new ProtocolParser();
        parser.MessageParsed += (_, m) => c.RaiseMessage(m);
        parser.FeedBytes(ProtocolEncoder.EncodePinActivity(33));
        Assert.Equal(new[] { 33 }, pins.ToArray());

        runtime.Reconnect();
        c.RaiseMessage(new PinActivityMessage(4));   // a retired connection's: ignored
        Assert.Equal(new[] { 33 }, pins.ToArray());
    }

    // ── I2C scan ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScanI2cAsync_ReturnsTheMatchingResult()
    {
        var runtime = StartConnected();
        var c = Connection(0);

        var scan = runtime.ScanI2cAsync(25, 26);
        Assert.Equal(new[] { ProtocolEncoder.EncodeI2cScan(25, 26) }, Frames(c, MessageType.I2cScan));

        c.RaiseMessage(new I2cScanResultMessage(21, 22, new[] { 0x3C }));   // another bus's
        Assert.False(scan.IsCompleted);

        c.RaiseMessage(new I2cScanResultMessage(25, 26, new[] { 0x3C, 0x3D }));
        Assert.Equal(new[] { 0x3C, 0x3D }, await Within(scan));
    }

    [Fact]
    public async Task ScanI2cAsync_TimesOutToNull()
    {
        var runtime = StartConnected();
        var scan = runtime.ScanI2cAsync(25, 26);

        _time.Advance(TimeSpan.FromMilliseconds(2999));
        Assert.False(scan.IsCompleted);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Null(await Within(scan));

        Connection(0).RaiseMessage(new I2cScanResultMessage(25, 26, new[] { 0x3C }));   // too late: harmless
    }

    [Fact]
    public async Task ScanI2cAsync_NoConnection_IsNull()
    {
        var runtime = StartConnected();
        runtime.Disconnect();
        Assert.Null(await Within(runtime.ScanI2cAsync(25, 26)));
    }

    [Fact]
    public async Task ScanI2cAsync_Dispose_CompletesWithNull()
    {
        var runtime = StartConnected();
        var scan = runtime.ScanI2cAsync(25, 26);
        runtime.Dispose();
        Assert.Null(await Within(scan));
        Assert.Null(await Within(runtime.ScanI2cAsync(25, 26)));
    }

    [Fact]
    public async Task ScanI2cAsync_Cancelled_IsCancelled()
    {
        var runtime = StartConnected();
        using var cts = new CancellationTokenSource();
        var scan = runtime.ScanI2cAsync(25, 26, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(scan));
    }

    // ── PinDrive and Try the lights ──────────────────────────────────────────────────────────

    [Fact]
    public void DrivePin_SendsThePinDriveFrame()
    {
        var runtime = StartConnected();
        Assert.True(runtime.DrivePin(12, high: true, TimeSpan.FromMilliseconds(300)));
        Assert.Equal(new[] { Drive(12, true, 300) }, Frames(Connection(0), MessageType.PinDrive));

        runtime.Disconnect();
        Assert.False(runtime.DrivePin(12, high: true, TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public void LightTry_Steps()
    {
        Assert.Equal(new[]
        {
            new LightStep(TimeSpan.Zero, 5, false, TimeSpan.FromMilliseconds(300)),
            new LightStep(TimeSpan.FromMilliseconds(300), 5, true, TimeSpan.FromMilliseconds(300)),
            new LightStep(TimeSpan.FromMilliseconds(600), 5, false, TimeSpan.FromMilliseconds(300)),
        }, LightTry.Steps(5, null, activeLow: true));

        Assert.Equal(new[]
        {
            new LightStep(TimeSpan.Zero, 4, true, TimeSpan.FromMilliseconds(600)),
            new LightStep(TimeSpan.Zero, 18, false, TimeSpan.FromMilliseconds(600)),
            new LightStep(TimeSpan.FromMilliseconds(700), 18, true, TimeSpan.FromMilliseconds(600)),
            new LightStep(TimeSpan.FromMilliseconds(700), 4, false, TimeSpan.FromMilliseconds(600)),
        }, LightTry.Steps(4, 18, activeLow: false));
    }

    [Fact]
    public void TryLights_SendsEachStepAtItsTime()
    {
        var runtime = StartConnected();
        var c = Connection(0);

        runtime.TryLights(LightTry.Steps(4, 18, activeLow: false));
        Assert.Equal(new[] { Drive(4, true, 600), Drive(18, false, 600) }, Frames(c, MessageType.PinDrive));

        _time.Advance(TimeSpan.FromMilliseconds(699));
        Assert.Equal(2, Frames(c, MessageType.PinDrive).Count);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(new[] { Drive(4, true, 600), Drive(18, false, 600), Drive(18, true, 600), Drive(4, false, 600) },
            Frames(c, MessageType.PinDrive));

        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(4, Frames(c, MessageType.PinDrive).Count);
    }

    [Fact]
    public void TryLights_AgainCancelsPendingSteps()
    {
        var runtime = StartConnected();
        var c = Connection(0);

        runtime.TryLights(LightTry.Steps(4, 18, activeLow: false));
        runtime.TryLights(LightTry.Steps(5, null, activeLow: true));
        _time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(new[] { Drive(4, true, 600), Drive(18, false, 600), Drive(5, false, 300), Drive(5, true, 300), Drive(5, false, 300) },
            Frames(c, MessageType.PinDrive));
    }
}
