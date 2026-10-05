using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Volco.App.ViewModels;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Firmware;
using Volco.Core.Runtime;
using Volco.Core.Tests.Firmware;
using Xunit;

namespace Volco.App.Tests.ViewModels;

/// <summary>Spec §8.6 and §12.4: the Controller page's firmware part.</summary>
public sealed class ControllerFirmwareTests : IDisposable
{
    private static readonly FirmwareCatalog Catalog = FirmwareCatalog.Load(MemoryFirmwareSource.Valid());
    private readonly TestRig _rig = new(options: new VolcoRuntimeOptions
    {
        Link = new ControllerLinkOptions(TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.FromHours(1)),
        Firmware = Catalog,
    });
    private readonly RecordingDispatcher _ui = new();
    private readonly FakeTimeProvider _time = new();
    private readonly FakeFlasher _flasher;
    private ControllerViewModel? _vm;

    public ControllerFirmwareTests()
    {
        _rig.Config.Config.Connection.Serial.ComPort = "COM3";
        _flasher = new FakeFlasher(() => _rig.Runtime.IsSuspended);
    }

    public void Dispose()
    {
        _vm?.Dispose();
        _rig.Dispose();
    }

    private ControllerViewModel Vm(FirmwareCatalog? catalog = null)
        => _vm = new ControllerViewModel(_rig.Config, _rig.Runtime, _ui,
            new FirmwareService(_rig.Runtime, _flasher, catalog ?? Catalog, _time), _time);

    private void Until(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            _ui.RunAll();
            if (condition()) return;
            if (sw.ElapsedMilliseconds > 5000) throw new TimeoutException();
            Thread.Sleep(5);
        }
    }

    private static HelloMessage Hello(string fw) => new(ProtocolVersion.Current, ChipFamily.Esp32, fw,
        ControllerConfigState.None, 0, 5, new[] { IndicatorKind.Led });

    [Fact]
    public void WithoutFirmware_OrNotSerial_TheButtonsAreDisabledWithAReason()
    {
        var vm = Vm(FirmwareCatalog.Empty);
        Assert.False(vm.CanUseFirmware);
        Assert.Equal("This build of Volco has no firmware.", vm.FirmwareHint);
        vm.Dispose();

        vm = Vm();
        Assert.True(vm.CanUseFirmware);
        Assert.Null(vm.FirmwareHint);
        _rig.Config.Config.Connection.Type = ConnectionType.WebSocket;   // not saved: no restart
        Assert.False(vm.CanUseFirmware);
        Assert.Equal("Connect the controller with USB and set the connection to Serial.", vm.FirmwareHint);
    }

    [Fact]
    public void Check_ShowsTheProbeMessage_AndOffersInstall()
    {
        var vm = Vm();
        _flasher.ProbeResult = new ProbeResult(ProbeKind.VolcoMatches);
        vm.CheckFirmwareCommand.Execute(null);
        Until(() => vm.FirmwareMessage is not null);
        Assert.Equal("Volco's firmware is installed but didn't answer. Reinstall it?", vm.FirmwareMessage);
        Assert.True(vm.OfferInstall);
        Assert.False(vm.IsFirmwareRunning);
        Assert.Equal("probe COM3", _flasher.Calls.Single());
    }

    [Fact]
    public void Install_Succeeds_AndOffersATest()
    {
        var vm = Vm();
        vm.InstallFirmwareCommand.Execute(null);
        Until(() => _flasher.Calls.Count == 1 && !_rig.Runtime.IsSuspended && _rig.Connection!.ConnectCount == 1
                    && _rig.Connection.Status == ConnectionStatus.Disconnected);
        _rig.Connection!.SetStatus(ConnectionStatus.Connected);
        _rig.Connection.RaiseMessage(Hello("3f9c2a1b5d07"));
        Until(() => vm.FirmwareMessage is not null);
        Assert.Equal("Installed. The controller is running firmware 3f9c2a1b5d07.", vm.FirmwareMessage);
        Assert.True(vm.OfferTest);
        Assert.Equal("flash COM3 Install", _flasher.Calls.Single());
        Assert.StartsWith("Firmware 3f9c2a1b5d07 (from Volco ", vm.FirmwareText);
    }

    [Fact]
    public void TheNoHelloHint_After5sOfSilenceOnSerial()
    {
        var vm = Vm();
        _rig.Connection!.SetStatus(ConnectionStatus.Connected);   // HelloRequest sent, no Hello
        _time.Advance(TimeSpan.FromSeconds(1));
        _ui.RunAll();
        Assert.Null(vm.NoHelloHint);
        _time.Advance(TimeSpan.FromSeconds(5));
        _ui.RunAll();
        Assert.Equal("No Volco controller answered on COM3. Check what's connected?", vm.NoHelloHint);

        _rig.Connection.RaiseMessage(Hello("dev"));
        _time.Advance(TimeSpan.FromSeconds(1));
        _ui.RunAll();
        Assert.Null(vm.NoHelloHint);
        Assert.Equal("Development firmware", vm.FirmwareText);
    }

    [Fact]
    public void TheNoHelloHint_NeverOnWebSocket()
    {
        _rig.Config.Config.Connection.Type = ConnectionType.WebSocket;
        var vm = Vm();
        _rig.Connection!.SetStatus(ConnectionStatus.Connected);
        _time.Advance(TimeSpan.FromSeconds(1));
        _time.Advance(TimeSpan.FromSeconds(6));
        _ui.RunAll();
        Assert.Null(vm.NoHelloHint);
    }

    [Fact]
    public void Update_IsShownWhenTheControllerRunsAnotherBuild()
    {
        var vm = Vm();
        _rig.Connection!.SetStatus(ConnectionStatus.Connected);
        _rig.Connection.RaiseMessage(Hello("0000aaaa1111"));
        _ui.RunAll();
        Assert.True(vm.ShowUpdateFirmware);
        _rig.Connection.RaiseMessage(Hello("3f9c2a1b5d07"));
        _ui.RunAll();
        Assert.False(vm.ShowUpdateFirmware);
    }
}
