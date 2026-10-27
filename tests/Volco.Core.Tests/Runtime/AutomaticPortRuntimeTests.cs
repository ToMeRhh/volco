using Microsoft.Extensions.Time.Testing;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Runtime;
using Volco.Core.Tests.Audio;
using Volco.Core.Tests.Controller;
using Xunit;

namespace Volco.Core.Tests.Runtime;

/// <summary>The runtime on the Automatic USB port (spec §10.2): an empty ComPort tries the ports
/// until one answers, the link waits while it does, and the port that answered is remembered.</summary>
public sealed class AutomaticPortRuntimeTests : IDisposable
{
    private static readonly HelloMessage Hello = new(ProtocolVersion.Current, 2, new[] { IndicatorKind.Led });

    private readonly string _dir;
    private readonly ConfigManager _config;
    private readonly FakeTimeProvider _time = new();
    private readonly FakePorts _ports = new();
    private readonly List<(string Name, FakeControllerConnection Fake)> _opened = new();
    private int _usbPortChanges;
    private VolcoRuntime? _runtime;

    public AutomaticPortRuntimeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "volco-auto-port-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigManager(Path.Combine(_dir, "config.json"));
        _config.Load();
    }

    public void Dispose()
    {
        _runtime?.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static SerialPortDescription Usb(string name) => new(name, "USB Serial Device", 0x10C4, 0xEA60);
    private static SerialPortDescription Ch340(string name) => new(name, "USB-SERIAL CH340", 0x1A86, 0x7523);

    private VolcoRuntime Start(TimeSpan? grace = null, params SerialPortDescription[] ports)
    {
        _ports.Ports = ports;
        _runtime = new VolcoRuntime(_config, new FakeAudioEngine(), true, connectionFactory: null, new VolcoRuntimeOptions
        {
            // A fast HelloRequest retry (checked on each tick) on real time: it has to reach each port
            // Automatic tries.
            Link = new ControllerLinkOptions(TimeSpan.FromMilliseconds(20), TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(10)),
            Time = _time,
            ConnectGrace = grace ?? TimeSpan.Zero,
            SerialFactory = name =>
            {
                var fake = new FakeControllerConnection(new ConnectionConfig { Serial = new SerialConfig { ComPort = name } })
                {
                    StatusOnConnect = TransportStatus.Open,
                };
                lock (_opened) _opened.Add((name, fake));
                return fake;
            },
        }, custom: null, serialPorts: _ports);
        _runtime.UsbPortChanged += (_, _) => Interlocked.Increment(ref _usbPortChanges);
        _runtime.Start();
        return _runtime;
    }

    private FakeControllerConnection Opened(string name) { lock (_opened) return _opened.Last(o => o.Name == name).Fake; }
    private List<string> OpenedNames { get { lock (_opened) return _opened.Select(o => o.Name).ToList(); } }

    private static bool IsHelloRequest(byte[] frame) => frame.Length > 2 && frame[2] == (byte)MessageType.HelloRequest;

    private void AdvanceSeconds(int seconds)
    {
        for (int i = 0; i < seconds; i++) _time.Advance(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void NoPortsIsSearching()
    {
        var runtime = Start(TimeSpan.FromSeconds(3));
        Assert.Equal(LinkState.Waiting, runtime.LinkStatus.State);

        AdvanceSeconds(3);

        Assert.Equal(LinkState.Searching, runtime.LinkStatus.State);
        Assert.Null(runtime.UsbPort);
        Assert.True(runtime.HasSerialPortInfo);
    }

    [Fact]
    public void SilentCh340IsNoAnswerOnIt()
    {
        var runtime = Start(null, Usb("COM4"), Ch340("COM7"));

        AdvanceSeconds(5);   // SilentAfter has passed, but the round is still trying COM4
        Assert.Equal(new LinkStatus(LinkState.Waiting, LinkActivity.Connecting), runtime.LinkStatus);
        Assert.Null(runtime.UsbPort);

        AdvanceSeconds(1);   // the round ends on the CH340

        Assert.Equal(LinkState.NoAnswer, runtime.LinkStatus.State);
        Assert.Equal("COM7", runtime.UsbPort);
        Assert.True(_usbPortChanges > 0);
    }

    [Fact]
    public void SeveralWithoutCh340IsNoAnswerWithoutPort()
    {
        var runtime = Start(null, Usb("COM3"), Usb("COM4"));

        AdvanceSeconds(6);

        Assert.Equal(LinkState.NoAnswer, runtime.LinkStatus.State);
        Assert.Null(runtime.UsbPort);
    }

    [Fact]
    public async Task AnswerWritesLastPort()
    {
        var runtime = Start(null, Usb("COM2"), Usb("COM3"));
        AdvanceSeconds(3);   // COM2 stays silent; COM3 is tried next

        // ControllerLink's HelloRequest retry reaches the port Automatic is trying now.
        var com3 = Opened("COM3");
        await ConfigServiceKit.Until(() => com3.SentFrames.Any(IsHelloRequest));
        com3.RaiseMessage(Hello);

        Assert.Equal(LinkState.Connected, runtime.LinkStatus.State);
        Assert.Equal("COM3", runtime.UsbPort);
        Assert.Equal("COM3", _config.Config.Connection.Serial.LastPort);
        Assert.Equal("", _config.Config.Connection.Serial.ComPort);
        Assert.True(_usbPortChanges > 0);

        _config.FlushPendingSave();
        var reloaded = new ConfigManager(_config.ConfigPath);
        reloaded.Load();
        Assert.Equal("COM3", reloaded.Config.Connection.Serial.LastPort);
    }

    [Fact]
    public void ManualPortUnchanged()
    {
        _config.Config.Connection.Serial.ComPort = "COM5";
        var runtime = Start(null, Ch340("COM7"));

        Assert.Equal(new[] { "COM5" }, OpenedNames);
        Assert.Equal(0, _ports.ListCount);   // a manual port lists nothing
        Assert.Equal("COM5", runtime.UsbPort);
    }

    [Fact]
    public void OtherTransportsHaveNoUsbPort()
    {
        _config.Config.Connection.Type = ConnectionType.Udp;
        _config.Config.Connection.Udp.ListenPort = 0;
        var runtime = Start(null, Ch340("COM7"));

        Assert.Null(runtime.UsbPort);
        Assert.Empty(OpenedNames);
    }

    private sealed class FakePorts : ISerialPortInfo
    {
        private int _listCount;
        public IReadOnlyList<SerialPortDescription> Ports { get; set; } = [];
        public int ListCount => Volatile.Read(ref _listCount);

        public IReadOnlyList<SerialPortDescription> List()
        {
            Interlocked.Increment(ref _listCount);
            return Ports;
        }
    }
}
