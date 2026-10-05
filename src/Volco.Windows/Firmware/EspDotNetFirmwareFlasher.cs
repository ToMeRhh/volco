using System.Diagnostics;
using System.IO.Ports;
using System.Security.Cryptography;
using EspDotNet;
using EspDotNet.Communication;
using EspDotNet.Config;
using EspDotNet.Loaders.SoftLoader;
using EspDotNet.Tools;
using EspDotNet.Tools.Firmware;
using Volco.Core.Diagnostics;
using Volco.Core.Firmware;

namespace Volco.Windows.Firmware;

/// <summary>
/// The USB flasher (spec §8.6) over EspDotNet 5.0.2: DTR/RTS into the ROM bootloader, chip check,
/// the stub at 460800 baud when it can, MD5 probe of the app region, part-by-part writes with an MD5
/// verify (never a merged image, so Update never touches NVS), 0xFF over NVS for Install, reset.
/// </summary>
public sealed class EspDotNetFirmwareFlasher : IFirmwareFlasher
{
    private const int RomBaud = 115200;
    private const int FastBaud = 460800;
    private static readonly TimeSpan PortOpenRetry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(6);

    public async Task<ProbeResult> ProbeAsync(string port, FirmwareImage bundled, IProgress<FlashProgress> progress, CancellationToken ct)
    {
        Session session;
        try { session = await Session.StartAsync(port, progress, ct).ConfigureAwait(false); }
        catch (NoDeviceException) { return new ProbeResult(ProbeKind.NoDevice); }
        await using (session)
        {
            if (session.Chip != ChipTypes.ESP32)
            {
                await session.ResetAsync().ConfigureAwait(false);
                return new ProbeResult(ProbeKind.UnsupportedChip, ChipName(session.Chip));
            }
            var app = bundled.App.Info;
            var md5 = Convert.ToHexString(await session.Soft!.SpiFlashMd5Async(app.Offset, app.Size, ct).ConfigureAwait(false)).ToLowerInvariant();
            await session.ResetAsync().ConfigureAwait(false);
            AppLog.Info($"Probe: app region MD5 {md5}, bundled {app.Md5}");
            return new ProbeResult(md5 == app.Md5 ? ProbeKind.VolcoMatches : ProbeKind.DifferentFirmware);
        }
    }

    public async Task FlashAsync(string port, FirmwareImage image, FlashMode mode, IProgress<FlashProgress> progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await using var session = await Session.StartAsync(port, progress, ct).ConfigureAwait(false);
        if (session.Chip != ChipTypes.ESP32)
        {
            await session.ResetAsync().ConfigureAwait(false);
            throw new UnsupportedChipException(ChipName(session.Chip));
        }
        ct.ThrowIfCancellationRequested();   // the last point a flash can be cancelled (spec §8.6)
        var none = CancellationToken.None;
        bool written = false;
        try
        {
            var upload = new FirmwareUploadTool(new FlashUploadDeflatedTool(session.Soft!, session.Device!));
            if (mode == FlashMode.Install)
            {
                // Clears old or foreign settings: exactly the NVS partition, never a chip erase.
                progress.Report(new FlashProgress(FlashStage.Erasing, 0));
                written = true;
                var ff = Enumerable.Repeat((byte)0xFF, (int)image.Manifest.NvsSize).ToArray();
                await upload.UploadFirmwareAsync(new FirmwareProvider(0,
                    new IFirmwareSegmentProvider[] { new FirmwareSegmentProvider(image.Manifest.NvsOffset, ff) }), none).ConfigureAwait(false);
                progress.Report(new FlashProgress(FlashStage.Erasing, 1));
            }

            progress.Report(new FlashProgress(FlashStage.Writing, 0));
            upload.Progress = new Progress<float>(p => progress.Report(new FlashProgress(FlashStage.Writing, p)));
            written = true;
            // Each part at its own offset (spec §8.6): the gaps, NVS included, are never written.
            await upload.UploadFirmwareAsync(new FirmwareProvider(0,
                image.Parts.Select(p => (IFirmwareSegmentProvider)new FirmwareSegmentProvider(p.Info.Offset, p.Data))), none).ConfigureAwait(false);

            for (int i = 0; i < image.Parts.Count; i++)
            {
                var part = image.Parts[i];
                progress.Report(new FlashProgress(FlashStage.Verifying, (double)i / image.Parts.Count));
                var md5 = Convert.ToHexString(await session.Soft!.SpiFlashMd5Async(part.Info.Offset, part.Info.Size, none)
                    .ConfigureAwait(false)).ToLowerInvariant();
                if (md5 != Convert.ToHexString(MD5.HashData(part.Data)).ToLowerInvariant())
                    throw new FirmwareFlashException($"verification failed for {part.Info.File}", written: true);
            }

            progress.Report(new FlashProgress(FlashStage.Restarting, 0));
            await session.ResetAsync().ConfigureAwait(false);
            AppLog.Info($"Flash ({mode}) of {image.Manifest.Version} on {port}: {sw.Elapsed.TotalSeconds:F1} s at {session.Baud} baud");
        }
        catch (FirmwareFlashException) { throw; }
        catch (Exception e)
        {
            throw new FirmwareFlashException(e.Message, written);
        }
    }

    private static string ChipName(ChipTypes chip) => chip switch
    {
        ChipTypes.ESP8266 => "ESP8266",
        ChipTypes.Unknown => "unknown chip",
        _ => chip.ToString().Replace("ESP32", "ESP32-").ToUpperInvariant(),
    };

    /// <summary>One open port with the bootloader entered, the chip detected and the stub running.</summary>
    private sealed class Session : IAsyncDisposable
    {
        private readonly SerialPort _port;
        private readonly Communicator _comm;
        private readonly ESPToolConfig _config;

        private Session(SerialPort port, Communicator comm, ESPToolConfig config)
            => (_port, _comm, _config) = (port, comm, config);

        public ChipTypes Chip { get; private set; }
        public DeviceConfig? Device { get; private set; }
        public SoftLoader? Soft { get; private set; }
        public int Baud => _port.BaudRate;

        public static async Task<Session> StartAsync(string portName, IProgress<FlashProgress> progress, CancellationToken ct)
        {
            progress.Report(new FlashProgress(FlashStage.Connecting, 0));
            var port = await OpenAsync(portName, ct).ConfigureAwait(false);
            var config = ConfigProvider.LoadDefaultConfig();
            var session = new Session(port, new Communicator(port), config);
            try
            {
                // A board that never answers must not hang the dialog: the timeout closes the port,
                // which ends any read the library is blocked in.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ConnectTimeout);
                using var reg = timeout.Token.Register(() => { try { port.Close(); } catch { /* closing */ } });
                await session.ConnectAsync(timeout.Token).ConfigureAwait(false);
                progress.Report(new FlashProgress(FlashStage.Connecting, 1));
                return session;
            }
            catch (Exception e)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                AppLog.Info($"No ESP32 bootloader on {portName}: {e.GetType().Name}: {e.Message}");
                throw new NoDeviceException($"No ESP32 found on {portName}.");
            }
        }

        private async Task ConnectAsync(CancellationToken ct)
        {
            var bootloader = await new BootloaderTool(_comm, _config.BootloaderSequence).StartBootloaderAsync(ct).ConfigureAwait(false);
            Device = await new ChipTypeDetectTool(bootloader, _config).DetectAndGetDeviceConfigAsync(ct).ConfigureAwait(false);
            Chip = Device.ChipType;
            if (Chip != ChipTypes.ESP32) return;
            Soft = await new SoftLoaderTool(_comm, new RamUploadTool(bootloader, Device))
                .StartAsync(DefaultFirmwareProviders.GetSoftloaderForDevice(Chip), ct).ConfigureAwait(false);
            try
            {
                await new ChangeBaudRateTool(Soft).ChangeBaudAsync(FastBaud, RomBaud, ct).ConfigureAwait(false);
                _port.BaudRate = FastBaud;   // the caller reconfigures the port (EspDotNet's contract)
                await Task.Delay(50, ct).ConfigureAwait(false);
                _comm.ClearBuffer();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                AppLog.Info($"Staying at {RomBaud} baud: {e.Message}");
                _port.BaudRate = RomBaud;
            }
        }

        private static async Task<SerialPort> OpenAsync(string name, CancellationToken ct)
        {
            // The released connection may still hold the port for a moment (spec §8.6 step 1).
            var sw = Stopwatch.StartNew();
            while (true)
            {
                var port = new SerialPort(name, RomBaud) { ReadTimeout = 3000, WriteTimeout = 3000 };
                try
                {
                    port.Open();
                    return port;
                }
                catch (Exception e) when (e is UnauthorizedAccessException or IOException)
                {
                    port.Dispose();
                    if (sw.Elapsed > PortOpenRetry) throw new NoDeviceException($"{name} is in use or missing: {e.Message}");
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    port.Dispose();
                    throw new NoDeviceException($"{name} can't be opened: {e.Message}");
                }
            }
        }

        /// <summary>Hard reset via RTS into the new firmware.</summary>
        public async Task ResetAsync()
        {
            try { await new ResetDeviceTool(_comm, _config.ResetSequence).ResetAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception e) { AppLog.Error($"Reset failed: {e.Message}"); }
        }

        public ValueTask DisposeAsync()
        {
            try { _port.Close(); } catch { /* already closed */ }
            _port.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
