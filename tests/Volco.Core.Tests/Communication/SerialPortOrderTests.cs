using Volco.Core.Communication;
using Volco.Core.Config;
using Xunit;

namespace Volco.Core.Tests.Communication;

public class SerialPortOrderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "volco-port-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static SerialPortDescription P(string name, int vid, int pid) => new(name, name + " device", vid, pid);
    private static SerialPortDescription N(string name) => new(name);

    [Fact]
    public void Ch340FirstThenLastPortThenRest()
    {
        var ports = new[] { P("COM1", 0x10C4, 0xEA60), P("COM4", 0x0403, 0x6001), P("COM7", 0x1A86, 0x7523) };
        Assert.Equal(new[] { "COM7", "COM4", "COM1" },
            SerialPortOrder.Candidates(ports, lastPort: "COM4", haveInfo: true).Select(p => p.Name));
    }

    [Fact]
    public void NonUsbSkippedWithInfo()
        => Assert.DoesNotContain("COM5", SerialPortOrder.Candidates(new[]
        {
            P("COM3", 0x1A86, 0x7523), new SerialPortDescription("COM5", "Standard Serial over Bluetooth link"),
        }, null, true).Select(p => p.Name));

    [Fact]
    public void WithoutInfoEveryPortInNameOrder()
        => Assert.Equal(new[] { "COM3", "COM10" },
            SerialPortOrder.Candidates(new[] { N("COM10"), N("COM3") }, null, haveInfo: false).Select(p => p.Name));

    [Fact]
    public void BestCandidateIsCh340ElseOnlyPortElseNull()
    {
        Assert.Equal("COM7", SerialPortOrder.BestCandidate(new[] { P("COM1", 1, 1), P("COM7", 0x1A86, 0x7523) })!.Name);
        Assert.Equal("COM1", SerialPortOrder.BestCandidate(new[] { P("COM1", 1, 1) })!.Name);
        Assert.Null(SerialPortOrder.BestCandidate(new[] { P("COM1", 1, 1), P("COM2", 1, 2) }));
    }

    [Fact]
    public void MergeListsAPortTheSeamMissesByName()
    {
        var merged = SystemSerialPorts.Merge(new[] { P("COM3", 0x1A86, 0x7523) }, new[] { "COM3", "COM12", "COM4" });
        Assert.Equal(new[] { "COM3", "COM4", "COM12" }, merged.Select(p => p.Name));
        Assert.True(merged[0].IsCh340);
        Assert.False(merged[1].IsUsb);
    }

    [Fact]
    public void NewConfigIsAutomatic() => Assert.Equal("", new SerialConfig().ComPort);

    [Fact]
    public void LastPortRoundTrips()
    {
        var path = Path.Combine(_dir, "config.json");
        var cm = new ConfigManager(path);
        cm.Load();
        Assert.Null(cm.Config.Connection.Serial.LastPort);
        cm.Config.Connection.Serial.LastPort = "COM9";
        cm.Save();

        var again = new ConfigManager(path);
        again.Load();
        Assert.Equal("COM9", again.Config.Connection.Serial.LastPort);
    }
}
