using System.Collections.Concurrent;
using System.Diagnostics;
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Runtime;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.Core.Tests.Runtime;

/// <summary>Slider calibration through <see cref="VolcoRuntime"/> (slider-calibration spec, App
/// (Core)): the queries after a Hello, the reports, and a slider's input hold during a sweep.</summary>
public sealed class CalibrationRuntimeTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan QuietWindow = TimeSpan.FromMilliseconds(150);

    private static readonly VolcoRuntimeOptions Quiet = new()
    {
        Link = new ControllerLinkOptions(TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.FromHours(1)),
        ConnectGrace = TimeSpan.Zero,
    };

    private static readonly HelloMessage FiveSliders = new(ProtocolVersion.Current, 5,
        new[] { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair });

    private readonly FakeAudioEngine _engine = new();
    private readonly List<FakeControllerConnection> _connections = new();   // guarded by itself
    private readonly List<VolcoRuntime> _runtimes = new();
    private readonly string _dir;

    public CalibrationRuntimeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"volco-calibration-{Guid.NewGuid()}");
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

    /// <summary>A runtime with every slider on the default output, its controller connected and greeted.</summary>
    private VolcoRuntime StartConnected()
    {
        var config = new ConfigManager(Path.Combine(_dir, "config.json"));
        config.Load();
        foreach (var slider in config.Config.Sliders) slider.Type = SliderMappingType.Device;
        var runtime = new VolcoRuntime(config, _engine, true, c =>
        {
            var connection = new FakeControllerConnection(c);
            lock (_connections) _connections.Add(connection);
            return connection;
        }, Quiet);
        _runtimes.Add(runtime);
        runtime.Start();
        Connection(0).SetStatus(TransportStatus.Open);
        Connection(0).RaiseMessage(FiveSliders);
        return runtime;
    }

    private static bool WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > WaitTimeout) return condition();
            Thread.Sleep(5);
        }
        return true;
    }

    private static bool StaysUnchanged(Func<int> read)
    {
        var initial = read();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < QuietWindow)
        {
            if (read() != initial) return false;
            Thread.Sleep(5);
        }
        return read() == initial;
    }

    private int VolumeCalls => _engine.Snapshot(_engine.DeviceVolumeCalls).Count;

    private static bool Sent(FakeControllerConnection c, byte[] frame) => c.SentFrames.Any(f => f.AsSpan().SequenceEqual(frame));

    private static CalibrationMessage Report(int slider, bool sweeping, bool learned = true)
        => new(slider, learned, sweeping, autoCalibrate: true, low: 170, high: 2550);

    [Fact]
    public void QueriesEverySliderAfterAHello()
    {
        var runtime = StartConnected();
        var c = Connection(0);
        for (int i = 0; i < 5; i++) Assert.True(Sent(c, ProtocolEncoder.EncodeCalibrate(i, CalibrateAction.Query)), $"slider {i} not queried");
        Assert.False(Sent(c, ProtocolEncoder.EncodeCalibrate(5, CalibrateAction.Query)));

        c.RaiseMessage(Report(1, sweeping: false));
        c.RaiseMessage(Report(4, sweeping: false));
        var before = c.SentCount;
        c.RaiseMessage(new HelloMessage(ProtocolVersion.Current, 3, new[] { IndicatorKind.Led }));

        // Queried again, three this time; slider 4's state is gone with it, slider 1's stays until its answer.
        var queries = c.SentFrames.Skip(before).Where(f => f[2] == (byte)MessageType.Calibrate).ToList();
        Assert.Equal(Enumerable.Range(0, 3).Select(i => ProtocolEncoder.EncodeCalibrate(i, CalibrateAction.Query)), queries);
        Assert.Null(runtime.GetCalibration(4));
        Assert.NotNull(runtime.GetCalibration(1));
    }

    [Fact]
    public void AHelloOfAnotherVersion_QueriesNothing()
    {
        StartConnected();
        var c = Connection(0);
        var before = c.SentCount;
        c.RaiseMessage(HelloMessage.VersionOnly((byte)(ProtocolVersion.Current + 1)));
        Assert.DoesNotContain(c.SentFrames.Skip(before), f => f[2] == (byte)MessageType.Calibrate);
    }

    [Fact]
    public void ReportsUpdateTheStateAndRaiseTheEvent()
    {
        var runtime = StartConnected();
        var c = Connection(0);
        var events = new ConcurrentQueue<(int Slider, SliderCalibrationState State)>();
        runtime.CalibrationChanged += (_, e) => events.Enqueue(e);
        Assert.Null(runtime.GetCalibration(2));

        c.RaiseMessage(new CalibrationMessage(2, learned: false, sweeping: true, autoCalibrate: false, low: 4095, high: 0));
        var expected = new SliderCalibrationState(false, true, false, 4095, 0);
        Assert.Equal(expected, runtime.GetCalibration(2));
        Assert.Equal((2, expected), Assert.Single(events));

        c.RaiseMessage(new CalibrationMessage(2, learned: true, sweeping: false, autoCalibrate: false, low: 300, high: 3900));
        Assert.Equal(new SliderCalibrationState(true, false, false, 300, 3900), runtime.GetCalibration(2));
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void AReportFromARetiredConnection_IsIgnored()
    {
        var runtime = StartConnected();
        var old = Connection(0);
        runtime.Reconnect();
        old.RaiseMessage(Report(0, sweeping: false));
        Assert.Null(runtime.GetCalibration(0));
    }

    [Fact]
    public void SendCalibrate_SendsTheFrame_AndFailsWithoutAConnection()
    {
        var runtime = StartConnected();
        Assert.True(runtime.SendCalibrate(2, CalibrateAction.Start));
        Assert.True(Sent(Connection(0), ProtocolEncoder.EncodeCalibrate(2, CalibrateAction.Start)));

        runtime.Disconnect();
        Assert.False(runtime.SendCalibrate(2, CalibrateAction.Cancel));
    }

    [Fact]
    public void AHeldSliderDoesntReachAudio()
    {
        var runtime = StartConnected();
        var c = Connection(0);
        var raw = new ConcurrentQueue<RawInputEventArgs>();
        runtime.RawInput += (_, e) => raw.Enqueue(e);

        var hold = runtime.HoldSlider(0);
        Assert.True(runtime.IsSliderHeld(0));
        c.RaiseMessage(new SliderMessage(0, 4095));
        Assert.Equal((RawInputKind.Slider, 0, 4095), raw.Select(e => (e.Kind, e.Index, e.Value)).Single());
        Assert.True(StaysUnchanged(() => VolumeCalls));
        Assert.Equal(0, VolumeCalls);

        c.RaiseMessage(new SliderMessage(1, 4095));   // another slider still works
        Assert.True(WaitUntil(() => VolumeCalls > 0));

        hold.Dispose();
        hold.Dispose();   // idempotent
        Assert.False(runtime.IsSliderHeld(0));
        var calls = VolumeCalls;
        c.RaiseMessage(new SliderMessage(0, 0));
        Assert.True(WaitUntil(() => VolumeCalls > calls));
    }

    [Fact]
    public void AnEndingReportReleasesTheHold()
    {
        var runtime = StartConnected();
        var c = Connection(0);
        using var hold = runtime.HoldSlider(0);

        c.RaiseMessage(Report(0, sweeping: false));   // in flight from before the Start: not the sweep's end
        Assert.True(runtime.IsSliderHeld(0));
        c.RaiseMessage(Report(0, sweeping: true, learned: false));
        c.RaiseMessage(Report(1, sweeping: false));   // another slider's
        Assert.True(runtime.IsSliderHeld(0));

        c.RaiseMessage(Report(0, sweeping: false));
        Assert.False(runtime.IsSliderHeld(0));
        c.RaiseMessage(new SliderMessage(0, 4095));   // the fresh position after the sweep reaches audio
        Assert.True(WaitUntil(() => VolumeCalls > 0));
    }

    [Fact]
    public void DisposingAnOldHold_LeavesANewerOneOnTheSameSlider()
    {
        var runtime = StartConnected();
        var first = runtime.HoldSlider(3);
        using var second = runtime.HoldSlider(3);
        first.Dispose();
        Assert.True(runtime.IsSliderHeld(3));
    }

    [Fact]
    public void ConnectionReplacementReleasesTheHold()
    {
        var runtime = StartConnected();
        var old = Connection(0);
        using var hold = runtime.HoldSlider(2);

        runtime.Reconnect();

        Assert.False(runtime.IsSliderHeld(2));
        Assert.True(Sent(old, ProtocolEncoder.EncodeCalibrate(2, CalibrateAction.Cancel)), "the sweep was not cancelled");
    }

    [Fact]
    public void TheTransportClosing_ReleasesTheHold()
    {
        var runtime = StartConnected();
        using var hold = runtime.HoldSlider(2);
        Connection(0).SetStatus(TransportStatus.Failed);
        Assert.False(runtime.IsSliderHeld(2));
    }

    [Fact]
    public void Dispose_CancelsAHeldSweep()
    {
        var runtime = StartConnected();
        var c = Connection(0);
        runtime.HoldSlider(4);
        runtime.Dispose();
        Assert.True(Sent(c, ProtocolEncoder.EncodeCalibrate(4, CalibrateAction.Cancel)));
        Assert.False(runtime.IsSliderHeld(4));
        runtime.HoldSlider(4).Dispose();   // after dispose: an inert handle
        Assert.False(runtime.IsSliderHeld(4));
    }
}
