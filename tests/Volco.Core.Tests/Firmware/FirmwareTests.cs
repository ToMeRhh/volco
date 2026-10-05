using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Firmware;
using Volco.Core.Runtime;
using Volco.Core.Tests.Audio;
using Volco.Core.Tests.Runtime;
using Xunit;

namespace Volco.Core.Tests.Firmware;

public class FirmwareManifestTests
{
    [Fact]
    public void AValidBundle_Loads()
    {
        var catalog = FirmwareCatalog.Load(MemoryFirmwareSource.Valid());
        Assert.True(catalog.HasFirmware);
        Assert.Equal("3f9c2a1b5d07", catalog.BundledVersion);
        Assert.Equal(0x10000u, catalog.Esp32!.App.Info.Offset);
        Assert.Equal(4, catalog.Esp32.Parts.Count);
    }

    [Fact]
    public void APartOverlappingNvs_IsRejected()
    {
        var src = MemoryFirmwareSource.Valid(nvsOffset: 0xE000);   // boot_app0 sits at 0xE000
        var e = Assert.Throws<FirmwareManifestException>(() => FirmwareManifest.Parse(src.ManifestText));
        Assert.Contains("overlaps NVS", e.Message);
        Assert.False(FirmwareCatalog.Load(src).HasFirmware);
    }

    [Fact]
    public void OverlappingParts_AndNoApp_AreRejected()
    {
        var src = MemoryFirmwareSource.Valid();
        Assert.Throws<FirmwareManifestException>(() => FirmwareManifest.Parse(src.ManifestText.Replace("\"offset\":32768", "\"offset\":4100")));
        Assert.Throws<FirmwareManifestException>(() => FirmwareManifest.Parse(src.ManifestText.Replace("\"name\":\"app\"", "\"name\":\"main\"")));
        Assert.Throws<FirmwareManifestException>(() => FirmwareManifest.Parse("{}"));
        Assert.Throws<FirmwareManifestException>(() => FirmwareManifest.Parse("not json"));
    }

    [Fact]
    public void APartWithTheWrongMd5_IsRejected()
    {
        var src = MemoryFirmwareSource.Valid();
        src.Files["esp32/firmware.bin"][5] ^= 0xFF;
        Assert.Throws<FirmwareManifestException>(() => FirmwareImage.Load(src, "esp32"));
        Assert.False(FirmwareCatalog.Load(src).HasFirmware);
    }

    [Fact]
    public void NoBundle_NoFirmware()
    {
        var catalog = FirmwareCatalog.Load(EmptyFirmwareSource.Instance);
        Assert.False(catalog.HasFirmware);
        Assert.Null(catalog.BundledVersion);
    }
}

public sealed class FirmwareServiceTests : IDisposable
{
    private readonly FakeAudioEngine _engine = new();
    private readonly List<FakeControllerConnection> _connections = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"volco-fw-{Guid.NewGuid()}");
    private readonly FakeTimeProvider _time = new();
    private readonly ConfigManager _config;
    private readonly VolcoRuntime _runtime;
    private readonly FakeFlasher _flasher;
    private readonly FirmwareService _service;
    private readonly FirmwareCatalog _catalog = FirmwareCatalog.Load(MemoryFirmwareSource.Valid());

    public FirmwareServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _config = new ConfigManager(Path.Combine(_dir, "config.json"));
        _config.Load();
        _config.Config.Connection.Serial.ComPort = "COM3";
        _runtime = new VolcoRuntime(_config, _engine, true, cfg =>
        {
            var c = new FakeControllerConnection(cfg);
            lock (_connections) _connections.Add(c);
            return c;
        }, new VolcoRuntimeOptions
        {
            Link = new ControllerLinkOptions(TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.FromHours(1)),
            Firmware = _catalog,
        });
        _runtime.Start();
        _flasher = new FakeFlasher(() => _runtime.IsSuspended);
        _service = new FirmwareService(_runtime, _flasher, _catalog, _time);
    }

    public void Dispose()
    {
        _runtime.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private FakeControllerConnection Last { get { lock (_connections) return _connections[^1]; } }
    private int Count { get { lock (_connections) return _connections.Count; } }
    private static readonly IProgress<FlashProgress> NoProgress = new Progress<FlashProgress>();

    private static HelloMessage Hello(string fw) => new(ProtocolVersion.Current, ChipFamily.Esp32, fw,
        ControllerConfigState.None, 0, 5, new[] { IndicatorKind.Led });

    [Fact]
    public async Task Probe_SuspendsAndResumes_AndForwardsProgress()
    {
        var before = Last;
        var reports = new List<FlashProgress>();
        var outcome = await _service.ProbeAsync("COM3", new SyncProgress(reports.Add));
        Assert.True(_flasher.WasSuspended);
        Assert.Equal(1, before.DisposeCount);                 // released the port
        Assert.Equal(2, Count);                               // and opened a new connection after
        Assert.Equal(1, Last.ConnectCount);
        Assert.Single(reports);
        Assert.Equal("This ESP32 doesn't have this version of Volco's firmware.", outcome.Message);
        Assert.True(outcome.OfferInstall);
    }

    [Theory]
    [InlineData(ProbeKind.NoDevice, null, "No ESP32 found on COM3.", false)]
    [InlineData(ProbeKind.UnsupportedChip, "ESP32-S3", "This board is an ESP32-S3; this version of Volco only includes firmware for ESP32.", false)]
    [InlineData(ProbeKind.DifferentFirmware, null, "This ESP32 doesn't have this version of Volco's firmware.", true)]
    public async Task EveryProbeResult_HasItsMessage(ProbeKind kind, string? chip, string message, bool offer)
    {
        _flasher.ProbeResult = new ProbeResult(kind, chip);
        var outcome = await _service.ProbeAsync("COM3", NoProgress);
        Assert.Equal(message, outcome.Message);
        Assert.Equal(offer, outcome.OfferInstall);
    }

    [Fact]
    public async Task VolcoMatches_AndTheBoardAnswers_SaysItRunsTheFirmware()
    {
        _flasher.ProbeResult = new ProbeResult(ProbeKind.VolcoMatches);
        var probe = _service.ProbeAsync("COM3", NoProgress);
        await Until(() => Count == 2);
        Last.SetStatus(ConnectionStatus.Connected);
        Last.RaiseMessage(Hello("3f9c2a1b5d07"));
        var outcome = await probe;
        Assert.Equal("This controller runs Volco's firmware 3f9c2a1b5d07.", outcome.Message);
        Assert.False(outcome.OfferInstall);
    }

    [Fact]
    public async Task VolcoMatches_ButSilent_OffersAReinstall()
    {
        _flasher.ProbeResult = new ProbeResult(ProbeKind.VolcoMatches);
        var probe = _service.ProbeAsync("COM3", NoProgress);
        await Until(() => Count == 2);
        await Task.Delay(50);
        _time.Advance(TimeSpan.FromSeconds(10));
        var outcome = await probe;
        Assert.Equal("Volco's firmware is installed but didn't answer. Reinstall it?", outcome.Message);
        Assert.True(outcome.OfferInstall);
    }

    [Fact]
    public async Task Flash_Succeeds_WhenTheBundledBuildSaysHello()
    {
        var flash = _service.FlashAsync("COM3", FlashMode.Update, NoProgress);
        await Until(() => Count == 2);
        Last.SetStatus(ConnectionStatus.Connected);
        Last.RaiseMessage(Hello("3f9c2a1b5d07"));
        var outcome = await flash;
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal("Updated. The controller is running firmware 3f9c2a1b5d07.", outcome.Message);
        Assert.Contains("flash COM3 Update", _flasher.Calls);
        Assert.True(_flasher.WasSuspended);
    }

    [Fact]
    public async Task Flash_NoHelloWithin10s_SaysSo()
    {
        var flash = _service.FlashAsync("COM3", FlashMode.Install, NoProgress);
        await Until(() => Count == 2);
        Last.SetStatus(ConnectionStatus.Connected);
        Last.RaiseMessage(Hello("dev"));                       // not the bundled build
        await Task.Delay(50);
        _time.Advance(TimeSpan.FromSeconds(10));
        var outcome = await flash;
        Assert.False(outcome.Succeeded);
        Assert.Equal("The firmware was installed but the controller didn't answer. Check the connection settings.", outcome.Message);
    }

    [Fact]
    public async Task AFailureMidFlash_SaysSo_AndTheConnectionResumes()
    {
        _flasher.FlashError = new FirmwareFlashException("verification failed for firmware.bin", written: true);
        var outcome = await _service.FlashAsync("COM3", FlashMode.Install, NoProgress);
        Assert.Equal("Install failed: verification failed for firmware.bin. The controller won't work until the install succeeds. Try again.",
                     outcome.Message);
        Assert.False(_runtime.IsSuspended);
        Assert.Equal(2, Count);
        Assert.Equal(1, Last.ConnectCount);
    }

    [Fact]
    public async Task ACancelBeforeWriting_WritesNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var outcome = await _service.FlashAsync("COM3", FlashMode.Install, NoProgress, cts.Token);
        Assert.Equal("Cancelled. Nothing was written.", outcome.Message);
        Assert.False(_runtime.IsSuspended);
    }

    [Fact]
    public async Task ADifferentPort_IsSavedWhileSuspended_AndTheResumedConnectionUsesIt()
    {
        var flash = _service.FlashAsync("COM7", FlashMode.Install, NoProgress);
        await Until(() => Count == 2);
        Assert.Equal("COM7", Last.Config.Serial.ComPort);
        Assert.Equal(2, Count);                                // the save didn't open an extra connection
        Last.SetStatus(ConnectionStatus.Connected);
        Last.RaiseMessage(Hello("3f9c2a1b5d07"));
        Assert.True((await flash).Succeeded);

        var reloaded = new ConfigManager(_config.ConfigPath);
        reloaded.Load();
        Assert.Equal("COM7", reloaded.Config.Connection.Serial.ComPort);
    }

    [Fact]
    public async Task NotSerial_IsRefusedWithoutSuspending()
    {
        _config.Config.Connection.Type = ConnectionType.WebSocket;   // not saved: no restart
        var before = Count;
        var outcome = await _service.FlashAsync("COM3", FlashMode.Install, NoProgress);
        Assert.Equal(FirmwareService.SerialOnlyMessage, outcome.Message);
        Assert.Empty(_flasher.Calls);
        Assert.Equal(before, Count);
    }

    [Fact]
    public async Task ASecondOperationWhileBusy_IsRefused()
    {
        var gate = new TaskCompletionSource();
        _flasher.DuringFlash = () => gate.Task;
        var first = _service.FlashAsync("COM3", FlashMode.Update, NoProgress);
        await Until(() => _service.IsBusy && _flasher.Calls.Count == 1);
        Assert.Equal("Another firmware operation is running.", (await _service.ProbeAsync("COM3", NoProgress)).Message);
        gate.SetResult();
        await Until(() => Count == 2);
        Last.SetStatus(ConnectionStatus.Connected);
        Last.RaiseMessage(Hello("3f9c2a1b5d07"));
        Assert.True((await first).Succeeded);
        Assert.False(_service.IsBusy);
    }

    [Fact]
    public void WithoutFirmware_NothingIsAvailable()
    {
        var service = new FirmwareService(_runtime, _flasher, FirmwareCatalog.Empty, _time);
        Assert.False(service.IsAvailable);
        Assert.False(new FirmwareService(_runtime, null, _catalog, _time).IsAvailable);
        Assert.True(_service.IsAvailable);
    }

    // ── The runtime's suspension (spec §8.6) ─────────────────────────────────────────────

    [Fact]
    public void WhileSuspended_AConfigChangeOrReconnect_OpensNothing_AndResumeUsesTheCurrentConfig()
    {
        var first = Last;
        var suspension = _runtime.SuspendConnection();
        Assert.Equal(1, first.DisposeCount);
        _runtime.Reconnect();
        _config.Config.Connection.Serial.ComPort = "COM9";
        _config.Save();                                      // raises ConnectionConfigChanged
        Assert.Equal(1, Count);

        suspension.Dispose();
        suspension.Dispose();                                // idempotent
        Assert.Equal(2, Count);
        Assert.Equal("COM9", Last.Config.Serial.ComPort);
        Assert.Equal(1, Last.ConnectCount);
    }

    [Fact]
    public async Task ARestartRacingASuspension_IsRetired_WithoutConnect()
    {
        // A factory that blocks: StartConnection is past its first check when the suspension starts.
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var connections = new List<FakeControllerConnection>();
        var dir = Path.Combine(_dir, "race");
        Directory.CreateDirectory(dir);
        var cm = new ConfigManager(Path.Combine(dir, "config.json"));
        cm.Load();
        var block = false;
        using var runtime = new VolcoRuntime(cm, _engine, true, cfg =>
        {
            var c = new FakeControllerConnection(cfg);
            lock (connections) connections.Add(c);
            if (block) { entered.Set(); release.Wait(); }
            return c;
        });
        runtime.Start();
        block = true;
        var restart = Task.Run(runtime.Reconnect);
        Assert.True(entered.Wait(5000));
        var suspension = runtime.SuspendConnection();
        release.Set();
        Assert.Same(restart, await Task.WhenAny(restart, Task.Delay(5000)));

        FakeControllerConnection raced;
        lock (connections) raced = connections[^1];
        Assert.Equal(0, raced.ConnectCount);
        Assert.Equal(1, raced.DisposeCount);
        block = false;
        suspension.Dispose();
    }

    [Fact]
    public void ResumeAfterDispose_DoesNothing()
    {
        var suspension = _runtime.SuspendConnection();
        _runtime.Dispose();
        var count = Count;
        suspension.Dispose();
        Assert.Equal(count, Count);
    }

    [Fact]
    public void HelloReceived_IncludesVersionOnlyHellos_AndTheBannerOffersUpdate()
    {
        var hellos = new List<HelloMessage>();
        _runtime.HelloReceived += (_, h) => hellos.Add(h);
        Last.SetStatus(ConnectionStatus.Connected);
        Last.RaiseMessage(HelloMessage.VersionOnly(2));
        Assert.Single(hellos);
        Assert.Equal(BannerKind.Incompatible, _runtime.ControllerConfig.Banner.Kind);
        Assert.True(_runtime.ControllerConfig.Banner.Actions.HasFlag(BannerActions.Update));

        Last.RaiseMessage(Hello("0000aaaa1111"));
        Assert.True(_runtime.ControllerConfig.Banner.FirmwareUpdateAvailable);
        Last.RaiseMessage(Hello("3f9c2a1b5d07"));
        Assert.False(_runtime.ControllerConfig.Banner.FirmwareUpdateAvailable);
    }

    private static async Task Until(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > 5000) throw new TimeoutException();
            await Task.Delay(5);
        }
    }

    private sealed class SyncProgress(Action<FlashProgress> report) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value) => report(value);
    }
}
