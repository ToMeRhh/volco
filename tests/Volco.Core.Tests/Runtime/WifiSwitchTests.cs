using Microsoft.Extensions.Time.Testing;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Volco.Core.Runtime;
using Volco.Core.Tests.Audio;
using Volco.Core.Tests.Controller;
using Xunit;

namespace Volco.Core.Tests.Runtime;

/// <summary>The Connection dialog's Wi-Fi operations (spec §10.2, §10.3) over a runtime whose
/// fake transports are recorded with the kind they were built for. Config edits restart the
/// connection through the real 400 ms debounce; the waits run on fake time.</summary>
public sealed class WifiSwitchTests : IDisposable
{
    private const uint PcIp = 0xC0A80114;   // 192.168.1.20

    private readonly FakeAudioEngine _engine = new();
    private readonly List<(ConnectionType Type, int ListenPort, FakeControllerConnection Connection)> _connections = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"volco-wifi-{Guid.NewGuid()}");
    private readonly FakeTimeProvider _time = new();
    private readonly ConfigManager _config;
    private readonly VolcoRuntime _runtime;
    private readonly WifiSwitch _switch;

    /// <summary>The controller's saved config before each test: Wi-Fi off.</summary>
    private static readonly byte[] WifiOff = ControllerConfigCodec.Encode(new ControllerConfig
    {
        Screens = new ScreenBehaviour { Brightness = 100 },
    });

    /// <summary>The controller's saved config with Wi-Fi on HomeNet, sending to port 12345.</summary>
    private static readonly byte[] WifiOnHome = ControllerConfigCodec.Encode(new ControllerConfig
    {
        Screens = new ScreenBehaviour { Brightness = 100 },
        Udp = new Udp { Enabled = true, Ssid = "HomeNet", Password = "password1", PcIp = PcIp, PcPort = 12345, LocalPort = 12346 },
    });

    private static readonly WifiSettings Office = new("Office", "password2", PcIp, 12400, 12346);

    public WifiSwitchTests() : this(ConnectionType.Serial, WifiOff) { }

    private WifiSwitchTests(ConnectionType type, byte[] mirror)
    {
        Directory.CreateDirectory(_dir);
        _config = new ConfigManager(Path.Combine(_dir, "config.json"));
        _config.Load();
        _config.Config.Connection.Type = type;
        _config.Config.Connection.Serial.ComPort = "COM3";
        _config.Config.ControllerConfigBlob = Convert.ToBase64String(mirror);
        _runtime = new VolcoRuntime(_config, _engine, true, cfg =>
        {
            var c = new FakeControllerConnection(cfg);
            lock (_connections) _connections.Add((cfg.Type, cfg.Udp.ListenPort, c));
            return c;
        }, new VolcoRuntimeOptions
        {
            Link = new ControllerLinkOptions(TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.FromHours(1)),
            Time = _time,
        });
        _runtime.Start();
        _switch = new WifiSwitch(_runtime, a => { a(); return Task.CompletedTask; });
    }

    /// <summary>The fixture with Volco on Wi-Fi, connected, and the controller's Wi-Fi on HomeNet.</summary>
    private static WifiSwitchTests OnWifi()
    {
        var t = new WifiSwitchTests(ConnectionType.Udp, WifiOnHome);
        Greet(t.Last, ControllerConfigCodec.Crc32(WifiOnHome));
        return t;
    }

    public void Dispose()
    {
        _switch.Dispose();
        _runtime.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private int Count { get { lock (_connections) return _connections.Count; } }
    private (ConnectionType Type, int ListenPort, FakeControllerConnection Connection) At(int i) { lock (_connections) return _connections[i]; }
    private FakeControllerConnection Last => At(Count - 1).Connection;
    private ConnectionConfig Connection => _config.Config.Connection;

    private static HelloMessage StoredHello(uint crc) => new(ProtocolVersion.Current, ChipFamily.Esp32, "dev",
        ControllerConfigState.Stored, crc, 0, Array.Empty<IndicatorKind>());

    private static void Greet(FakeControllerConnection c, uint crc)
    {
        c.SetStatus(TransportStatus.Open);
        c.RaiseMessage(StoredHello(crc));
    }

    private static bool IsWrite(byte[] f) => f.Length > 3 && f[2] == 0x88;

    private static int Writes(FakeControllerConnection c) => c.SentFrames.Count(IsWrite);

    /// <summary>The blob of the last ConfigWrite burst on <paramref name="c"/> (begin, chunks, end).</summary>
    private static byte[] WrittenBlob(FakeControllerConnection c)
    {
        var burst = c.SentFrames.Last(IsWrite);
        var length = (burst[4] << 8) | burst[5];
        var blob = new List<byte>();
        var at = burst[1] + 3;                                   // past the begin frame
        while (at < burst.Length && blob.Count < length)
        {
            var len = burst[at + 1];
            if (burst[at + 2] == 0x89) blob.AddRange(burst.Skip(at + 2 + 4).Take(len - 4));
            at += len + 3;
        }
        return blob.ToArray();
    }

    /// <summary>The fake controller: waits for write number <paramref name="n"/> on
    /// <paramref name="c"/>, answers OK and, when given, restarts with a Hello of that CRC.</summary>
    private static async Task<byte[]> AnswerWrite(FakeControllerConnection c, int n = 1, uint? helloCrc = null,
                                                  ConfigResultCode code = ConfigResultCode.Ok)
    {
        await ConfigServiceKit.Until(() => Writes(c) >= n);
        var burst = c.SentFrames.Last(IsWrite);
        c.RaiseMessage(new ConfigResultMessage(burst[3], code, 0xFF, 0xFF));
        if (helloCrc is { } crc) c.RaiseMessage(StoredHello(crc));
        return WrittenBlob(c);
    }

    private static uint CrcOf(WifiSettings s)
    {
        var config = ControllerConfigCodec.Decode(WifiOff);
        config.Udp = new Udp { Enabled = true, Ssid = s.Ssid, Password = s.Password, PcIp = s.PcIp, PcPort = (uint)s.ListenPort, LocalPort = (uint)s.ControllerPort };
        return ControllerConfigCodec.Crc32(ControllerConfigCodec.Encode(config));
    }

    private async Task<T> Within<T>(Task<T> task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(10_000)));
        return await task;
    }

    // ── Save and switch ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SwitchStaysOnWifiOnHelloWithNewCrc()
    {
        Greet(Last, ControllerConfigCodec.Crc32(WifiOff));
        var steps = new List<WifiSwitchStep>();
        var progress = new SyncProgress(p => { lock (steps) steps.Add(p.Step); });
        var crc = CrcOf(Office);

        var run = _switch.SaveAndSwitchAsync(Office, progress);
        var sent = await AnswerWrite(Last, helloCrc: crc);
        var udp = ControllerConfigCodec.Decode(sent).Udp;
        Assert.True(udp.Enabled);
        Assert.Equal(("Office", "password2", PcIp, 12400u, 12346u), (udp.Ssid, udp.Password, udp.PcIp, udp.PcPort, udp.LocalPort));
        Assert.Equal(crc, ControllerConfigCodec.Crc32(sent));

        await ConfigServiceKit.Until(() => Count == 2);
        Assert.Equal((ConnectionType.Udp, 12400), (At(1).Type, At(1).ListenPort));
        Assert.False(run.IsCompleted);
        Greet(Last, crc);

        Assert.Equal(WifiSwitchOutcome.OnWifi, await Within(run));
        Assert.Equal(ConnectionType.Udp, Connection.Type);
        Assert.Equal(12400, Connection.Udp.ListenPort);
        Assert.Equal(Convert.ToBase64String(sent), _config.Config.ControllerConfigBlob);
        lock (steps) Assert.Equal(new[] { WifiSwitchStep.Saving, WifiSwitchStep.Restarting, WifiSwitchStep.WaitingForWifi }, steps.Distinct());
    }

    [Fact]
    public async Task SwitchFellBackOnHelloWithAnotherCrc()
    {
        Greet(Last, ControllerConfigCodec.Crc32(WifiOff));
        var run = _switch.SaveAndSwitchAsync(Office);
        await AnswerWrite(Last, helloCrc: CrcOf(Office));
        await ConfigServiceKit.Until(() => Count == 2);
        Greet(Last, ControllerConfigCodec.Crc32(WifiOnHome));

        Assert.Equal(WifiSwitchOutcome.FellBack, await Within(run));
        Assert.Equal(ConnectionType.Udp, Connection.Type);
    }

    [Fact]
    public async Task SwitchBackToUsbWithoutHello()
    {
        Greet(Last, ControllerConfigCodec.Crc32(WifiOff));
        var run = _switch.SaveAndSwitchAsync(Office);
        await AnswerWrite(Last, helloCrc: CrcOf(Office));
        await ConfigServiceKit.Until(() => Count == 2);

        _time.Advance(WifiSwitch.FallbackWindow + WifiSwitch.AnswerWait - TimeSpan.FromSeconds(1));
        await ConfigServiceKit.Settle();
        Assert.False(run.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(WifiSwitchOutcome.NoAnswer, await Within(run));
        Assert.Equal(ConnectionType.Serial, Connection.Type);
        Assert.Equal(12345, Connection.Udp.ListenPort);
        await ConfigServiceKit.Until(() => Count == 3);
        Assert.Equal(ConnectionType.Serial, At(2).Type);
    }

    [Fact]
    public async Task SwitchNeedsUsb()
    {
        // USB, but no controller answers.
        Assert.Equal(WifiSwitchOutcome.NeedsUsb, await _switch.SaveAndSwitchAsync(Office));
        Assert.Equal(0, Writes(Last));

        // On Wi-Fi and connected.
        using var t = OnWifi();
        Assert.Equal(WifiSwitchOutcome.NeedsUsb, await t._switch.SaveAndSwitchAsync(Office));
        Assert.Equal(0, Writes(t.Last));
        Assert.Equal(ConnectionType.Udp, t.Connection.Type);
    }

    [Fact]
    public async Task SaveFailureLeavesTransport()
    {
        Greet(Last, ControllerConfigCodec.Crc32(WifiOff));
        var run = _switch.SaveAndSwitchAsync(Office);
        await AnswerWrite(Last, code: ConfigResultCode.Invalid);

        Assert.Equal(WifiSwitchOutcome.SaveFailed, await Within(run));
        await ConfigServiceKit.Settle();
        await Task.Delay(500);   // past the config debounce: no restart was asked for
        Assert.Equal(ConnectionType.Serial, Connection.Type);
        Assert.Equal(1, Count);
        Assert.Equal(Convert.ToBase64String(WifiOff), _config.Config.ControllerConfigBlob);
    }

    [Fact]
    public async Task InvalidSettingsSendNothing()
    {
        Greet(Last, ControllerConfigCodec.Crc32(WifiOff));
        Assert.Equal(WifiSwitchOutcome.SaveFailed, await _switch.SaveAndSwitchAsync(Office with { Ssid = "" }));
        Assert.Equal(WifiSwitchOutcome.SaveFailed, await _switch.SaveAndSwitchAsync(Office with { Password = "short" }));
        Assert.Equal(0, Writes(Last));
    }

    [Fact]
    public async Task SaveWaitsOutTheQuietRead()
    {
        // A Hello whose CRC differs from the mirror only in Wi-Fi (the controller fell back)
        // starts the quiet read: the save started right after it waits for the read, which adopts
        // the controller's config, instead of failing as Busy.
        Greet(Last, ControllerConfigCodec.Crc32(WifiOnHome));
        await ConfigServiceKit.Until(() => _runtime.ControllerConfig.Operation == ConfigOperation.Reading);
        var run = _switch.SaveAndSwitchAsync(Office);
        await ConfigServiceKit.Settle();
        Assert.Equal(0, Writes(Last));

        var read = Last.SentFrames.Last(f => f.Length > 3 && f[2] == 0x87);
        foreach (var chunk in ConfigServiceKit.Chunks(read[3], WifiOnHome)) Last.RaiseMessage(chunk);

        await AnswerWrite(Last, helloCrc: CrcOf(Office));
        await ConfigServiceKit.Until(() => Count == 2);
        Greet(Last, CrcOf(Office));
        Assert.Equal(WifiSwitchOutcome.OnWifi, await Within(run));
    }

    /// <summary>A board whose setup isn't the mirror's (another controller, or one changed
    /// elsewhere): the read finds more than Wi-Fi differs, so nothing is adopted or sent.</summary>
    [Fact]
    public async Task SwitchUnderAnotherSetupSendsNothing()
    {
        var other = ControllerConfigCodec.Encode(new ControllerConfig { Screens = new ScreenBehaviour { Brightness = 50 } });
        Greet(Last, ControllerConfigCodec.Crc32(other));
        await ConfigServiceKit.Until(() => _runtime.ControllerConfig.Operation == ConfigOperation.Reading);
        var run = _switch.SaveAndSwitchAsync(Office);
        var read = Last.SentFrames.Last(f => f.Length > 3 && f[2] == 0x87);
        foreach (var chunk in ConfigServiceKit.Chunks(read[3], other)) Last.RaiseMessage(chunk);

        Assert.Equal(WifiSwitchOutcome.DifferentSetup, await Within(run));
        Assert.Equal(0, Writes(Last));
        Assert.Equal(Convert.ToBase64String(WifiOff), _config.Config.ControllerConfigBlob);
        Assert.Equal(ConnectionType.Serial, Connection.Type);
    }

    [Fact]
    public async Task SecondCallIsCancelled()
    {
        Greet(Last, ControllerConfigCodec.Crc32(WifiOff));
        var first = _switch.SaveAndSwitchAsync(Office);
        Assert.True(_switch.IsBusy);
        Assert.Equal(WifiSwitchOutcome.Cancelled, await _switch.SaveAndSwitchAsync(Office));
        Assert.Equal(WifiSwitchOutcome.Cancelled, await _switch.TurnOffAsync());

        await AnswerWrite(Last, code: ConfigResultCode.Invalid);
        Assert.Equal(WifiSwitchOutcome.SaveFailed, await Within(first));
        Assert.False(_switch.IsBusy);
    }

    /// <summary>Review Focus 3: Volco quits or the window hides mid-switch.</summary>
    [Fact]
    public async Task DisposeMidSwitchLeavesConfig()
    {
        Greet(Last, ControllerConfigCodec.Crc32(WifiOff));
        var run = _switch.SaveAndSwitchAsync(Office);
        await AnswerWrite(Last, helloCrc: CrcOf(Office));
        await ConfigServiceKit.Until(() => Count == 2);

        _switch.Dispose();
        Assert.Equal(WifiSwitchOutcome.Cancelled, await Within(run));
        _time.Advance(WifiSwitch.FallbackWindow + WifiSwitch.AnswerWait);
        await Task.Delay(500);
        Assert.Equal(ConnectionType.Udp, Connection.Type);   // as it was when cancelled
        Assert.Equal(12400, Connection.Udp.ListenPort);
        Assert.Equal(2, Count);
        Assert.Equal(WifiSwitchOutcome.Cancelled, await _switch.SaveAndSwitchAsync(Office));
        _switch.Dispose();
    }

    [Fact]
    public async Task CancelBeforeTheSwitchLeavesUsb()
    {
        Greet(Last, ControllerConfigCodec.Crc32(WifiOff));
        using var cts = new CancellationTokenSource();
        var run = _switch.SaveAndSwitchAsync(Office, ct: cts.Token);
        await ConfigServiceKit.Until(() => Writes(Last) == 1);
        cts.Cancel();
        Assert.Equal(WifiSwitchOutcome.Cancelled, await Within(run));
        await AnswerWrite(Last, helloCrc: CrcOf(Office));   // the save itself still ends on its own
        await Task.Delay(500);
        Assert.Equal(ConnectionType.Serial, Connection.Type);
        Assert.Equal(1, Count);
    }

    // ── Save over Wi-Fi ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OverWifiMovesTheListenPortAndStaysOnHelloWithNewCrc()
    {
        using var t = OnWifi();
        var crc = CrcOf(Office);
        var run = t._switch.SaveOverWifiAsync(Office);
        await AnswerWrite(t.Last);                       // OK; the restarted controller sends to 12400
        await ConfigServiceKit.Until(() => t.Count == 2);
        Assert.Equal((ConnectionType.Udp, 12400), (t.At(1).Type, t.At(1).ListenPort));
        Greet(t.Last, crc);

        Assert.Equal(WifiSwitchOutcome.OnWifi, await Within(run));
        Assert.Equal(ConnectionType.Udp, t.Connection.Type);
        Assert.Equal(12400, t.Connection.Udp.ListenPort);
        Assert.Equal(crc, ControllerConfigCodec.Crc32(t._runtime.ControllerConfig.Mirror!));   // the cut-short save, confirmed
    }

    [Fact]
    public async Task OverWifiIgnoresAHelloOnTheOldPort()
    {
        using var t = OnWifi();
        var crc = CrcOf(Office);
        var run = t._switch.SaveOverWifiAsync(Office);
        await AnswerWrite(t.Last);
        await ConfigServiceKit.Until(() => t.Connection.Udp.ListenPort == 12400);
        Assert.Equal(1, t.Count);                        // the restart waits for the debounce
        var old = t.Last;
        old.RaiseMessage(StoredHello(crc));               // a stale Hello on the old listener

        await ConfigServiceKit.Until(() => t.Count == 2);
        await ConfigServiceKit.Settle();
        Assert.False(run.IsCompleted);
        Greet(t.Last, ControllerConfigCodec.Crc32(WifiOnHome));
        Assert.Equal(WifiSwitchOutcome.FellBack, await Within(run));
    }

    [Fact]
    public async Task OverWifiFallbackReturnsToOldPort()
    {
        using var t = OnWifi();
        var run = t._switch.SaveOverWifiAsync(Office);
        await AnswerWrite(t.Last);
        await ConfigServiceKit.Until(() => t.Count == 2);
        Assert.Equal(12400, t.Connection.Udp.ListenPort);

        t._time.Advance(WifiSwitch.FallbackWindow);
        await ConfigServiceKit.Until(() => t.Count == 3);
        Assert.Equal((ConnectionType.Udp, 12345), (t.At(2).Type, t.At(2).ListenPort));
        Assert.Equal(12345, t.Connection.Udp.ListenPort);
        Assert.False(run.IsCompleted);
        Greet(t.Last, ControllerConfigCodec.Crc32(WifiOnHome));

        Assert.Equal(WifiSwitchOutcome.FellBack, await Within(run));
        Assert.Equal(ConnectionType.Udp, t.Connection.Type);
    }

    [Fact]
    public async Task OverWifiNoAnswerStaysOnWifi()
    {
        using var t = OnWifi();
        var run = t._switch.SaveOverWifiAsync(Office);
        await AnswerWrite(t.Last);
        await ConfigServiceKit.Until(() => t.Count == 2);
        t._time.Advance(WifiSwitch.FallbackWindow);
        await ConfigServiceKit.Until(() => t.Count == 3);
        await ConfigServiceKit.Settle();
        t._time.Advance(WifiSwitch.AnswerWait);

        Assert.Equal(WifiSwitchOutcome.NoAnswer, await Within(run));
        Assert.Equal((ConnectionType.Udp, 12345), (t.Connection.Type, t.Connection.Udp.ListenPort));
    }

    [Fact]
    public async Task OverWifiNeedsAWifiConnection()
    {
        Greet(Last, ControllerConfigCodec.Crc32(WifiOff));   // connected, but over USB
        Assert.Equal(WifiSwitchOutcome.NeedsUsb, await _switch.SaveOverWifiAsync(Office));
        Assert.Equal(0, Writes(Last));
    }

    // ── Turn Wi-Fi off ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TurnOffWaitsForUsbHelloThenSavesUdpOff()
    {
        using var t = OnWifi();
        var run = t._switch.TurnOffAsync();
        await ConfigServiceKit.Until(() => t.Count == 2);
        Assert.Equal(ConnectionType.Serial, t.At(1).Type);
        Assert.Equal(ConnectionType.Serial, t.Connection.Type);
        Assert.True(t._switch.TurnOffPending);

        t._time.Advance(TimeSpan.FromMinutes(10));   // no timeout
        await ConfigServiceKit.Settle();
        Assert.False(run.IsCompleted);
        Assert.Equal(0, Writes(t.Last));

        Greet(t.Last, ControllerConfigCodec.Crc32(WifiOnHome));
        var expected = ControllerConfigCodec.Decode(WifiOnHome);
        expected.Udp.Enabled = false;
        var blob = await AnswerWrite(t.Last, helloCrc: ControllerConfigCodec.Crc32(ControllerConfigCodec.Encode(expected)));
        Assert.False(ControllerConfigCodec.Decode(blob).Udp.Enabled);
        Assert.Equal("HomeNet", ControllerConfigCodec.Decode(blob).Udp.Ssid);

        Assert.Equal(WifiSwitchOutcome.BackOnUsb, await Within(run));
        Assert.False(t._switch.TurnOffPending);
        Assert.False(ControllerConfigCodec.Decode(t._runtime.ControllerConfig.Mirror!).Udp.Enabled);
        Assert.Equal(ConnectionType.Serial, t.Connection.Type);
    }

    [Fact]
    public async Task TurnOffUnderAnotherSetupSendsNothing()
    {
        using var t = OnWifi();
        var run = t._switch.TurnOffAsync();
        await ConfigServiceKit.Until(() => t.Count == 2);
        var other = ControllerConfigCodec.Encode(new ControllerConfig { Screens = new ScreenBehaviour { Brightness = 50 } });
        Greet(t.Last, ControllerConfigCodec.Crc32(other));   // another board on USB
        await ConfigServiceKit.Until(() => t.Last.SentFrames.Any(f => f.Length > 3 && f[2] == 0x87));
        var read = t.Last.SentFrames.Last(f => f.Length > 3 && f[2] == 0x87);
        foreach (var chunk in ConfigServiceKit.Chunks(read[3], other)) t.Last.RaiseMessage(chunk);

        Assert.Equal(WifiSwitchOutcome.DifferentSetup, await Within(run));
        Assert.Equal(0, Writes(t.Last));
        Assert.Equal(ConnectionType.Serial, t.Connection.Type);
    }

    [Fact]
    public async Task TurnOffRefusedFromTheSimulator()
    {
        Greet(Last, ControllerConfigCodec.Crc32(WifiOff));
        _config.Config.Connection.Type = ConnectionType.WebSocket;
        Assert.Equal(WifiSwitchOutcome.Cancelled, await Within(_switch.TurnOffAsync()));
        Assert.False(_switch.TurnOffPending);
        Assert.False(_switch.IsBusy);
        Assert.Equal(ConnectionType.WebSocket, Connection.Type);
        await Task.Delay(500);
        Assert.Equal(1, Count);
        Assert.Equal(0, Writes(Last));
    }

    [Fact]
    public async Task TurnOffOverUsbSavesAtOnce()
    {
        using var t = new WifiSwitchTests(ConnectionType.Serial, WifiOnHome);
        Greet(t.Last, ControllerConfigCodec.Crc32(WifiOnHome));
        var steps = new List<WifiSwitchStep>();
        var run = t._switch.TurnOffAsync(new SyncProgress(p => { lock (steps) steps.Add(p.Step); }));
        Assert.False(t._switch.TurnOffPending);   // nothing to wait for
        var expected = ControllerConfigCodec.Decode(WifiOnHome);
        expected.Udp.Enabled = false;
        var blob = await AnswerWrite(t.Last, helloCrc: ControllerConfigCodec.Crc32(ControllerConfigCodec.Encode(expected)));
        Assert.False(ControllerConfigCodec.Decode(blob).Udp.Enabled);
        Assert.Equal("HomeNet", ControllerConfigCodec.Decode(blob).Udp.Ssid);

        Assert.Equal(WifiSwitchOutcome.BackOnUsb, await Within(run));
        Assert.False(ControllerConfigCodec.Decode(t._runtime.ControllerConfig.Mirror!).Udp.Enabled);
        Assert.Equal(ConnectionType.Serial, t.Connection.Type);
        Assert.Equal(1, t.Count);   // the transport was never touched
        lock (steps) Assert.Equal(new[] { WifiSwitchStep.Saving, WifiSwitchStep.Restarting }, steps.Distinct());
    }

    [Fact]
    public async Task TurnOffOverUsbNeedsAController()
    {
        using var t = new WifiSwitchTests(ConnectionType.Serial, WifiOnHome);   // no Hello
        Assert.Equal(WifiSwitchOutcome.NeedsUsb, await Within(t._switch.TurnOffAsync()));
        Assert.Equal(0, Writes(t.Last));
        Assert.False(t._switch.IsBusy);
    }

    [Fact]
    public async Task TurnOffOverUsbAlreadyOffSendsNothing()
    {
        Greet(Last, ControllerConfigCodec.Crc32(WifiOff));
        Assert.Equal(WifiSwitchOutcome.BackOnUsb, await Within(_switch.TurnOffAsync()));
        Assert.Equal(0, Writes(Last));
    }

    [Fact]
    public async Task TurnOffOverUsbUnderAnotherSetupSendsNothing()
    {
        using var t = new WifiSwitchTests(ConnectionType.Serial, WifiOnHome);
        var other = ControllerConfigCodec.Encode(new ControllerConfig { Screens = new ScreenBehaviour { Brightness = 50 } });
        Greet(t.Last, ControllerConfigCodec.Crc32(other));
        await ConfigServiceKit.Until(() => t.Last.SentFrames.Any(f => f.Length > 3 && f[2] == 0x87));
        var read = t.Last.SentFrames.Last(f => f.Length > 3 && f[2] == 0x87);
        foreach (var chunk in ConfigServiceKit.Chunks(read[3], other)) t.Last.RaiseMessage(chunk);
        Assert.Equal(WifiSwitchOutcome.DifferentSetup, await Within(t._switch.TurnOffAsync()));
        Assert.Equal(0, Writes(t.Last));
    }

    [Fact]
    public async Task PickingTheSimulatorDropsAPendingTurnOff()
    {
        using var t = OnWifi();
        var run = t._switch.TurnOffAsync();
        await ConfigServiceKit.Until(() => t.Count == 2);

        t.Connection.Type = ConnectionType.WebSocket;     // the user picks the simulator
        t._config.RequestSave(connectionChanged: true);
        Assert.Equal(WifiSwitchOutcome.Cancelled, await Within(run));
        Assert.False(t._switch.TurnOffPending);

        t.Connection.Type = ConnectionType.Serial;        // then USB again
        t._config.RequestSave(connectionChanged: true);
        await ConfigServiceKit.Until(() => t.Count == 4);
        Greet(t.Last, ControllerConfigCodec.Crc32(WifiOnHome));
        await ConfigServiceKit.Settle();
        Assert.Equal(0, Writes(t.Last));
    }

    [Fact]
    public async Task CancelTurnOffDropsIt()
    {
        using var t = OnWifi();
        var run = t._switch.TurnOffAsync();
        await ConfigServiceKit.Until(() => t.Count == 2);
        Assert.True(t._switch.TurnOffPending);
        t._switch.CancelTurnOff();

        Assert.Equal(WifiSwitchOutcome.Cancelled, await Within(run));
        Assert.False(t._switch.TurnOffPending);
        Greet(t.Last, ControllerConfigCodec.Crc32(WifiOnHome));
        await ConfigServiceKit.Settle();
        Assert.Equal(0, Writes(t.Last));
        Assert.Equal(ConnectionType.Serial, t.Connection.Type);
    }

    [Fact]
    public async Task TurnOffFailedSaveStaysOnUsb()
    {
        using var t = OnWifi();
        var run = t._switch.TurnOffAsync();
        await ConfigServiceKit.Until(() => t.Count == 2);
        Greet(t.Last, ControllerConfigCodec.Crc32(WifiOnHome));
        await AnswerWrite(t.Last, code: ConfigResultCode.StorageFailed);

        Assert.Equal(WifiSwitchOutcome.SaveFailed, await Within(run));
        Assert.Equal(ConnectionType.Serial, t.Connection.Type);
    }

    [Fact]
    public async Task DisposeCancelsAPendingTurnOff()
    {
        using var t = OnWifi();
        var run = t._switch.TurnOffAsync();
        await ConfigServiceKit.Until(() => t.Count == 2);
        t._switch.Dispose();

        Assert.Equal(WifiSwitchOutcome.Cancelled, await Within(run));
        Greet(t.Last, ControllerConfigCodec.Crc32(WifiOnHome));
        await ConfigServiceKit.Settle();
        Assert.Equal(0, Writes(t.Last));
        Assert.Equal(ConnectionType.Serial, t.Connection.Type);
    }

    // ── Prefill ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FromMirrorReadsTheSavedWifi()
    {
        Assert.Equal(new WifiSettings("HomeNet", "password1", PcIp, 12999, 12346), WifiSwitch.FromMirror(WifiOnHome, 12999));
        Assert.Null(WifiSwitch.FromMirror(WifiOff, 12345));
        Assert.Null(WifiSwitch.FromMirror(null, 12345));
        Assert.Null(WifiSwitch.FromMirror(new byte[] { 0xFF, 0xFF }, 12345));
    }

    /// <summary>Reports on the calling thread (Progress&lt;T&gt; posts to the pool, out of order).</summary>
    private sealed class SyncProgress(Action<(WifiSwitchStep Step, TimeSpan? Left)> report) : IProgress<(WifiSwitchStep Step, TimeSpan? Left)>
    {
        public void Report((WifiSwitchStep Step, TimeSpan? Left) value) => report(value);
    }
}
