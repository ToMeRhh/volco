using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Controller;

namespace Volco.Core.Tests.Controller;

/// <summary>Records every burst <see cref="ControllerConfigService"/> sends.</summary>
public sealed class FakeSessionLink : IConfigSessionLink
{
    private readonly object _lock = new();
    private readonly List<byte[]> _bursts = new();
    private int _calls;

    public bool Attached { get; set; } = true;
    public ConnectionType ConnectionType { get; set; } = ConnectionType.Serial;

    public IReadOnlyList<byte[]> Bursts { get { lock (_lock) return _bursts.ToList(); } }
    public int Calls => Volatile.Read(ref _calls);

    public bool SendRaw(byte[] burst)
    {
        Interlocked.Increment(ref _calls);
        if (!Attached) return false;
        lock (_lock) _bursts.Add(burst);
        return true;
    }
}

/// <summary>A service over a fake link, fake time and a config in a temp dir.</summary>
public sealed class ConfigServiceKit : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"volco-cfgsvc-{Guid.NewGuid()}");

    public ConfigServiceKit(string? bundledFirmware = null)
    {
        Directory.CreateDirectory(_dir);
        Config = new ConfigManager(Path.Combine(_dir, "config.json"));
        Config.Load();
        Service = new ControllerConfigService(Config, Link, Time, () => bundledFirmware);
    }

    public FakeSessionLink Link { get; } = new();
    public FakeTimeProvider Time { get; } = new();
    public ConfigManager Config { get; }
    public ControllerConfigService Service { get; }

    /// <summary>A valid config blob (screens { brightness: 100 }).</summary>
    public static readonly byte[] Small = { 0x32, 0x02, 0x08, 0x64 };

    /// <summary>A 600-byte valid blob: a long preset name inside the limit would be too short, so
    /// this is opaque bytes; reads don't decode.</summary>
    public static byte[] Opaque(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 7 + 1)).ToArray();

    public static HelloMessage Hello(ControllerConfigState state, uint crc, string fw = "dev")
        => new(ProtocolVersion.Current, ChipFamily.Esp32, fw, state, crc, 0, Array.Empty<IndicatorKind>());

    /// <summary>The seq of a ConfigRead or ConfigWrite burst (both carry it right after the type).</summary>
    public static byte SeqOf(byte[] burst) => burst[3];

    public byte LastSeq => SeqOf(Link.Bursts[^1]);

    public static IEnumerable<ConfigDataMessage> Chunks(byte seq, byte[] blob, uint? crc = null)
    {
        var c = crc ?? (blob.Length == 0 ? 0u : ControllerConfigCodec.Crc32(blob));
        if (blob.Length == 0) { yield return new ConfigDataMessage(seq, 0, 0, 0, Array.Empty<byte>()); yield break; }
        for (int off = 0; off < blob.Length; off += 240)
            yield return new ConfigDataMessage(seq, (ushort)blob.Length, (ushort)off, c, blob[off..Math.Min(blob.Length, off + 240)]);
    }

    public static ConfigResultMessage Result(byte seq, ConfigResultCode code, byte item = 0xFF, byte field = 0xFF)
        => new(seq, code, item, field);

    public void SetMirror(byte[] blob) => Config.Config.ControllerConfigBlob = Convert.ToBase64String(blob);

    /// <summary>Does <paramref name="act"/>, then waits until the operation is waiting again.</summary>
    public async Task Step(Action act)
    {
        var before = Service.Waits;
        act();
        await Until(() => Service.Waits > before);
    }

    public void Deliver(IEnumerable<ControllerMessage> messages)
    {
        foreach (var m in messages) Service.OnMessage(m);
    }

    public static async Task Until(Func<bool> condition, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("condition not met");
            await Task.Delay(5);
        }
    }

    /// <summary>Gives background continuations a chance to run (to prove something did NOT happen).</summary>
    public static Task Settle() => Task.Delay(100);

    public void Dispose()
    {
        Service.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
