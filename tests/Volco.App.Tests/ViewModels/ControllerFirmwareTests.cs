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
    private readonly FakeTimeProvider _time;
    private readonly TestRig _rig;
    private readonly RecordingDispatcher _ui = new();
    private readonly FakeFlasher _flasher;
    private ControllerViewModel? _vm;

    public ControllerFirmwareTests()
    {
        _time = new FakeTimeProvider();
        _rig = new TestRig(options: new VolcoRuntimeOptions
        {
            Link = new ControllerLinkOptions(TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.FromHours(1)),
            Firmware = Catalog,
            Time = _time,
        });
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
            new FirmwareService(_rig.Runtime, _flasher, catalog ?? Catalog, _time));

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
        _flasher.ProbeResult = new ProbeResult(ProbeKind.DifferentFirmware);
        vm.CheckFirmwareCommand.Execute(null);
        Until(() => vm.FirmwareMessage is not null);
        Assert.Equal("This ESP32 doesn't have this version of Volco's firmware.", vm.FirmwareMessage);
        Assert.True(vm.OfferInstall);
        Assert.False(vm.IsFirmwareRunning);
        Assert.Equal("probe COM3", _flasher.Calls.Single());
    }

    [Fact]
    public void Install_Succeeds_AndOffersATest()
    {
        var vm = Vm();
        vm.InstallFirmwareCommand.Execute(null);
        Assert.True(vm.ConfirmInstallPending);               // it asks first: Install wipes the setup
        Assert.Empty(_flasher.Calls);
        vm.ConfirmInstallCommand.Execute(null);
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
    public void ASilentController_GetsTheNoAnswerBanner_WithCheckFirmware_AndTheCurrentPort()
    {
        var vm = Vm();
        _rig.Connection!.SetStatus(ConnectionStatus.Connected);   // HelloRequest sent, no Hello
        _ui.RunAll();
        Assert.True(vm.IsWaiting);
        Assert.False(vm.ShowEditor);
        _time.Advance(TimeSpan.FromSeconds(5));
        _ui.RunAll();
        Assert.True(_rig.Runtime.ControllerSilent);
        Assert.False(vm.IsWaiting);
        Assert.Equal("No controller answering", vm.StatusText);
        Assert.StartsWith("No Volco controller answered on COM3.", vm.BannerText);
        Assert.True(vm.ShowCheckFirmware);

        // Another port, then an unplug and replug: the text follows, and the wait starts over.
        _rig.Config.Config.Connection.Serial.ComPort = "COM5";
        _rig.Config.Save();
        _rig.Connection!.SetStatus(ConnectionStatus.Disconnected);
        _ui.RunAll();
        Assert.Null(vm.BannerText);
        _rig.Connection.SetStatus(ConnectionStatus.Connected);
        _ui.RunAll();
        Assert.False(_rig.Runtime.ControllerSilent);
        _time.Advance(TimeSpan.FromSeconds(5));
        _ui.RunAll();
        Assert.StartsWith("No Volco controller answered on COM5.", vm.BannerText);

        _rig.Connection.RaiseMessage(Hello("dev"));
        _ui.RunAll();
        Assert.False(_rig.Runtime.ControllerSilent);
        Assert.NotEqual(Volco.Core.Controller.BannerKind.NoAnswer, vm.Banner.Kind);
        Assert.Equal("Development firmware", vm.FirmwareText);
        Assert.True(vm.ShowEditor);
    }

    [Fact]
    public void NoNoAnswerBanner_OnWebSocket()
    {
        _rig.Config.Config.Connection.Type = ConnectionType.WebSocket;
        var vm = Vm();
        _rig.Connection!.SetStatus(ConnectionStatus.Connected);
        _time.Advance(TimeSpan.FromSeconds(6));
        _ui.RunAll();
        Assert.False(_rig.Runtime.ControllerSilent);
        Assert.Null(vm.BannerText);
    }

    [Fact]
    public void ADetach_ForgetsTheLastHello()
    {
        var vm = Vm();
        _rig.Connection!.SetStatus(ConnectionStatus.Connected);
        _rig.Connection.RaiseMessage(Hello("dev"));
        _ui.RunAll();
        Assert.Equal("Development firmware", vm.FirmwareText);
        _rig.Connection.SetStatus(ConnectionStatus.Disconnected);
        _ui.RunAll();
        Assert.Equal("", vm.FirmwareText);
        Assert.Null(_rig.Runtime.ControllerConfig.LastHello);
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
