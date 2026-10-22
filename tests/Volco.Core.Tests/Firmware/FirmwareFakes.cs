using System.Security.Cryptography;
using System.Text;
using Volco.Core.Firmware;

namespace Volco.Core.Tests.Firmware;

/// <summary>An in-memory firmware bundle: four parts and a manifest, like manifest.py writes.</summary>
public sealed class MemoryFirmwareSource : IFirmwareSource
{
    public readonly Dictionary<string, byte[]> Files = new();

    public static MemoryFirmwareSource Valid(string version = "3f9c2a1b5d07", uint nvsOffset = 0x9000)
    {
        var src = new MemoryFirmwareSource();
        var parts = new (string Name, string File, uint Offset, int Size)[]
        {
            ("bootloader", "bootloader.bin", 0x1000, 100), ("partitions", "partitions.bin", 0x8000, 64),
            ("boot_app0", "boot_app0.bin", 0xE000, 32), ("app", "firmware.bin", 0x10000, 1000),
        };
        var json = new StringBuilder($"{{\"chip\":\"esp32\",\"version\":\"{version}\",\"displayVersion\":\"1.2.0\",\"parts\":[");
        foreach (var (name, file, offset, size) in parts)
        {
            var data = Enumerable.Range(0, size).Select(i => (byte)(i + name.Length)).ToArray();
            src.Files[$"esp32/{file}"] = data;
            json.Append($"{{\"name\":\"{name}\",\"file\":\"{file}\",\"offset\":{offset},\"size\":{size},\"md5\":\"{Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant()}\"}},");
        }
        json.Length--;
        json.Append($"],\"nvs\":{{\"offset\":{nvsOffset},\"size\":20480}}}}");
        src.Files["esp32/firmware-manifest.json"] = Encoding.UTF8.GetBytes(json.ToString());
        return src;
    }

    public string ManifestText
    {
        get => Encoding.UTF8.GetString(Files["esp32/firmware-manifest.json"]);
        set => Files["esp32/firmware-manifest.json"] = Encoding.UTF8.GetBytes(value);
    }

    public Stream? Open(string path) => Files.TryGetValue(path, out var d) ? new MemoryStream(d) : null;
}

/// <summary>A flasher that records what it was asked and checks the runtime is suspended.</summary>
public sealed class FakeFlasher : IFirmwareFlasher
{
    private readonly Func<bool> _isSuspended;
    public FakeFlasher(Func<bool> isSuspended) => _isSuspended = isSuspended;

    public ProbeResult ProbeResult { get; set; } = new(ProbeKind.DifferentFirmware);
    public Exception? FlashError { get; set; }
    public List<string> Calls { get; } = new();
    public bool WasSuspended { get; private set; } = true;
    public Func<Task>? DuringFlash { get; set; }

    /// <summary>Awaited inside the probe, while the port is held.</summary>
    public Func<Task>? DuringProbe { get; set; }

    public async Task<ProbeResult> ProbeAsync(string port, FirmwareImage bundled, IProgress<FlashProgress> progress, CancellationToken ct)
    {
        Calls.Add($"probe {port}");
        WasSuspended &= _isSuspended();
        progress.Report(new FlashProgress(FlashStage.Connecting, 1));
        if (DuringProbe is not null) await DuringProbe();
        return ProbeResult;
    }

    public async Task FlashAsync(string port, FirmwareImage image, FlashMode mode, IProgress<FlashProgress> progress, CancellationToken ct)
    {
        Calls.Add($"flash {port} {mode}");
        WasSuspended &= _isSuspended();
        ct.ThrowIfCancellationRequested();
        progress.Report(new FlashProgress(FlashStage.Writing, 0.5));
        if (DuringFlash is not null) await DuringFlash();
        if (FlashError is not null) throw FlashError;
    }
}
