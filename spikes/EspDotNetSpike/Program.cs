// SPIKE ONLY. Usage (run from the folder holding the firmware parts and parts.txt):
//   EspDotNetSpike probe <COMx>          chip, stub, MD5 of each part's region vs the local file, >=4MB proxy
//   EspDotNetSpike flash <COMx>          NVS MD5 before, write the 4 parts, verify MD5s, NVS MD5 after, reset
//   EspDotNetSpike wipe-nvs <COMx>       write 0xFF over 0x9000..0xDFFF, verify, reset
//   EspDotNetSpike serial-errors <COMx>  open at 115200, reset via RTS, log ErrorReceived/data for 6 s
using System.Diagnostics;
using System.IO.Ports;
using System.Security.Cryptography;
using EspDotNet.Communication;
using EspDotNet.Config;
using EspDotNet.Tools;
using EspDotNet.Tools.Firmware;

const uint NvsOffset = 0x9000, NvsSize = 0x5000;

if (args.Length < 2) { Console.WriteLine("usage: EspDotNetSpike probe|flash|wipe-nvs|serial-errors COMx"); return 2; }
var (cmd, portName) = (args[0], args[1]);
var sw = Stopwatch.StartNew();
void Log(string s) => Console.WriteLine($"[{sw.Elapsed.TotalSeconds,6:F2}s] {s}");

static List<(uint Offset, string File, byte[] Data)> LoadParts() =>
    File.ReadAllLines("parts.txt").Where(l => l.Trim().Length > 0).Select(l =>
    {
        var p = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (Convert.ToUInt32(p[0], 16), p[1], File.ReadAllBytes(p[1]));
    }).ToList();

static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

if (cmd == "serial-errors")
{
    using var p = new SerialPort(portName, 115200);
    var bytes = 0; var aa = 0; var text = new System.Text.StringBuilder();
    p.ErrorReceived += (_, e) => Log($"ErrorReceived {e.EventType}");
    p.DataReceived += (_, _) =>
    {
        var buf = new byte[p.BytesToRead]; var n = p.Read(buf, 0, buf.Length);
        bytes += n; aa += buf.Take(n).Count(x => x == 0xAA);
        if (text.Length < 400) text.Append(System.Text.Encoding.ASCII.GetString(buf, 0, n));
    };
    p.Open();
    Log("port open; resetting via RTS (EN), like a reboot");
    p.DtrEnable = false; p.RtsEnable = true; await Task.Delay(100); p.RtsEnable = false;
    await Task.Delay(6000);
    Log($"received {bytes} bytes, {aa} x 0xAA; first text: {text.ToString().Replace("\r", "").Replace("\n", " | ")}");
    return 0;
}

var config = ConfigProvider.LoadDefaultConfig();
using var port = new SerialPort(portName, 115200);
port.Open();
var comm = new Communicator(port);
var ct = CancellationToken.None;

Log("entering bootloader");
var bootloader = await new BootloaderTool(comm, config.BootloaderSequence).StartBootloaderAsync(ct);
var device = await new ChipTypeDetectTool(bootloader, config).DetectAndGetDeviceConfigAsync(ct);
Log($"chip: {device.ChipType}");
var stub = DefaultFirmwareProviders.GetSoftloaderForDevice(device.ChipType);
var soft = await new SoftLoaderTool(comm, new RamUploadTool(bootloader, device)).StartAsync(stub, ct);
Log("stub running");

async Task<string> Md5(uint offset, uint size) => Hex(await soft.SpiFlashMd5Async(offset, size, ct));

switch (cmd)
{
    case "probe":
        foreach (var (off, file, data) in LoadParts())
            Log($"{file} @0x{off:X}: flash={await Md5(off, (uint)data.Length)} local={Hex(MD5.HashData(data))}");
        try { Log($">=4MB proxy: MD5(0x3FF000, 0x1000)={await Md5(0x3FF000, 0x1000)}"); }
        catch (Exception e) { Log($">=4MB proxy FAILED: {e.Message}"); }
        break;

    case "flash":
        Log($"NVS before: {await Md5(NvsOffset, NvsSize)}");
        var parts = LoadParts();
        var upload = new FirmwareUploadTool(new FlashUploadDeflatedTool(soft, device))
        {
            Progress = new Progress<float>(p => { if ((int)(p * 100) % 25 == 0) Log($"progress {p:P0}"); }),
        };
        await upload.UploadFirmwareAsync(
            new FirmwareProvider(0, parts.Select(p => (IFirmwareSegmentProvider)new FirmwareSegmentProvider(p.Offset, p.Data))), ct);
        Log("written");
        foreach (var (off, file, data) in parts)
            Log($"verify {file}: {(await Md5(off, (uint)data.Length) == Hex(MD5.HashData(data)) ? "MATCH" : "MISMATCH")}");
        Log($"NVS after: {await Md5(NvsOffset, NvsSize)}");
        break;

    case "wipe-nvs":
        var ff = Enumerable.Repeat((byte)0xFF, (int)NvsSize).ToArray();
        await new FirmwareUploadTool(new FlashUploadDeflatedTool(soft, device)).UploadFirmwareAsync(
            new FirmwareProvider(0, new[] { (IFirmwareSegmentProvider)new FirmwareSegmentProvider(NvsOffset, ff) }), ct);
        Log($"NVS after wipe: {await Md5(NvsOffset, NvsSize)} expected {Hex(MD5.HashData(ff))}");
        break;

    default:
        Log($"unknown command {cmd}");
        return 2;
}

await new ResetDeviceTool(comm, config.ResetSequence).ResetAsync(ct);
Log("reset; done");
return 0;
