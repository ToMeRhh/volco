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

/// <summary>Spec §8.6 and §12.4: the firmware part of the Hardware page's controller logic.</summary>
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
    public void WithoutFirmware_OrNotSerial_FirmwareCantRun()
    {
        var vm = Vm(FirmwareCatalog.Empty);
        Assert.False(vm.CanUseFirmware);
        vm.Dispose();

        vm = Vm();
        Assert.True(vm.CanUseFirmware);
        _rig.Config.Config.Connection.Type = ConnectionType.WebSocket;   // not saved: no restart
        Assert.False(vm.CanUseFirmware);
    }

    [Fact]
    public void Check_ShowsTheProbeMessage()
    {
        var vm = Vm();
        _flasher.ProbeResult = new ProbeResult(ProbeKind.DifferentFirmware);
        vm.CheckFirmwareCommand.Execute(null);
        Until(() => vm.FirmwareMessage is not null);
        Assert.Equal("This ESP32 doesn't have this version of Volco's firmware.", vm.FirmwareMessage);
        Assert.Equal(vm.FirmwareMessage, vm.Failure);   // a warning: the card shows it
        Assert.True(vm.OfferInstall);                    // and its button is Reinstall firmware…
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
                    && _rig.Connection.Status == TransportStatus.Closed);
        _rig.Connection!.SetStatus(TransportStatus.Open);
        _rig.Connection.RaiseMessage(Hello("3f9c2a1b5d07"));
        Until(() => vm.FirmwareMessage is not null);
        Assert.Equal("Installed. The controller is running firmware 3f9c2a1b5d07.", vm.FirmwareMessage);
        Assert.False(vm.OfferTest);   // Install erased the setup: "Set up" comes before a test
        Assert.Equal("flash COM3 Install", _flasher.Calls.Single());
        // The controller has no setup now: setting it up is the next step.
        Assert.True(vm.IsNotSetUp);
    }

    [Fact]
    public void Install_AsksFirst_AndCancelLeavesThePageAsItWas()
    {
        var vm = Vm();
        _flasher.ProbeResult = new ProbeResult(ProbeKind.DifferentFirmware);
        vm.CheckFirmwareCommand.Execute(null);
        Until(() => vm.FirmwareMessage is not null);

        vm.InstallFirmwareCommand.Execute(null);
        Assert.True(vm.ConfirmInstallPending);
        Assert.Equal("This ESP32 doesn't have this version of Volco's firmware.", vm.FirmwareMessage);

        vm.CancelInstallCommand.Execute(null);
        Assert.False(vm.ConfirmInstallPending);
        Assert.Equal("This ESP32 doesn't have this version of Volco's firmware.", vm.FirmwareMessage);
        Assert.Equal("probe COM3", _flasher.Calls.Single());
    }

    [Fact]
    public void ASilentController_GetsTheNoAnswerBanner_WithCheckFirmware_AndTheCurrentPort()
    {
        var vm = Vm();
        _rig.Connection!.SetStatus(TransportStatus.Open);   // HelloRequest sent, no Hello
        _ui.RunAll();
        Assert.Equal(LinkState.Waiting, _rig.Runtime.LinkStatus.State);
        Assert.False(vm.CanEdit);
        _time.Advance(TimeSpan.FromSeconds(5));
        _ui.RunAll();
        Assert.True(_rig.Runtime.ControllerSilent);
        Assert.Equal("Controller not responding", vm.Connection.StatusText);
        Assert.Equal(Volco.Core.Controller.BannerKind.NoAnswer, vm.Banner.Kind);
        Assert.True(vm.Banner.Actions.HasFlag(Volco.Core.Controller.BannerActions.CheckFirmware));
        Assert.True(vm.CanUseFirmware);

        // Another port, then an unplug and replug: the text follows, and the wait starts over.
        _rig.Config.Config.Connection.Serial.ComPort = "COM5";
        _rig.Config.Save();
        _rig.Connection!.SetStatus(TransportStatus.Closed);
        _ui.RunAll();
        Assert.NotEqual(Volco.Core.Controller.BannerKind.NoAnswer, vm.Banner.Kind);
        _rig.Connection.SetStatus(TransportStatus.Open);
        _ui.RunAll();
        Assert.False(_rig.Runtime.ControllerSilent);
        _time.Advance(TimeSpan.FromSeconds(5));
        _ui.RunAll();
        Assert.Equal(Volco.Core.Controller.BannerKind.NoAnswer, vm.Banner.Kind);

        _rig.Connection.RaiseMessage(Hello("dev"));
        _ui.RunAll();
        Assert.False(_rig.Runtime.ControllerSilent);
        Assert.NotEqual(Volco.Core.Controller.BannerKind.NoAnswer, vm.Banner.Kind);
        Assert.True(vm.Banner.DevelopmentFirmware);
        Assert.Equal(ControllerState.Ready, _rig.Runtime.ControllerState);
    }

    [Fact]
    public void NoNoAnswerBanner_OnWebSocket()
    {
        _rig.Config.Config.Connection.Type = ConnectionType.WebSocket;
        var vm = Vm();
        _rig.Connection!.SetStatus(TransportStatus.Open);
        _time.Advance(TimeSpan.FromSeconds(6));
        _ui.RunAll();
        Assert.False(_rig.Runtime.ControllerSilent);
        Assert.NotEqual(Volco.Core.Controller.BannerKind.NoAnswer, vm.Banner.Kind);
    }

    [Fact]
    public void ADetach_ForgetsTheLastHello()
    {
        var vm = Vm();
        _rig.Connection!.SetStatus(TransportStatus.Open);
        _rig.Connection.RaiseMessage(Hello("dev"));
        _ui.RunAll();
        Assert.Equal("ESP32", vm.ChipText);
        _rig.Connection.SetStatus(TransportStatus.Closed);
        _ui.RunAll();
        Assert.Equal("", vm.ChipText);
        Assert.Null(_rig.Runtime.ControllerConfig.LastHello);
    }

    [Fact]
    public void Update_IsShownWhenTheControllerRunsAnotherBuild()
    {
        var vm = Vm();
        _rig.Connection!.SetStatus(TransportStatus.Open);
        _rig.Connection.RaiseMessage(Hello("0000aaaa1111"));
        _ui.RunAll();
        Assert.True(vm.FirmwareUpdateAvailable);
        _rig.Connection.RaiseMessage(Hello("3f9c2a1b5d07"));
        _ui.RunAll();
        Assert.False(vm.FirmwareUpdateAvailable);
    }

    [Fact]
    public void Update_Succeeds_AndOffersATest()
    {
        var vm = Vm();
        vm.UpdateFirmwareCommand.Execute(null);
        Until(() => _flasher.Calls.Count == 1 && !_rig.Runtime.IsSuspended && _rig.Connection!.ConnectCount == 1
                    && _rig.Connection.Status == TransportStatus.Closed);
        _rig.Connection!.SetStatus(TransportStatus.Open);
        _rig.Connection.RaiseMessage(Hello("3f9c2a1b5d07"));
        Until(() => vm.FirmwareMessage is not null);
        Assert.Equal("Updated. The controller is running firmware 3f9c2a1b5d07.", vm.FirmwareMessage);
        Assert.True(vm.OfferTest);   // the setup survived an Update
        Assert.Equal("flash COM3 Update", _flasher.Calls.Single());
    }

    [Fact]
    public void TheResult_GoesWithTheConnection_ThePage_OrTheController()
    {
        var vm = Vm();
        vm.InstallFirmwareCommand.Execute(null);
        vm.ConfirmInstallCommand.Execute(null);
        Until(() => _flasher.Calls.Count == 1 && !_rig.Runtime.IsSuspended && _rig.Connection!.ConnectCount == 1
                    && _rig.Connection.Status == TransportStatus.Closed);
        _rig.Connection!.SetStatus(TransportStatus.Open);
        _rig.Connection.RaiseMessage(Hello("3f9c2a1b5d07"));
        Until(() => vm.FirmwareMessage is not null);

        // The board is unplugged: the result no longer applies.
        _rig.Connection.SetStatus(TransportStatus.Closed);
        Until(() => vm.FirmwareMessage is null);
        Assert.False(vm.OfferTest);
    }

    [Fact]
    public void ChangingToUdp_DisablesFirmware_AndDropsTheResult()
    {
        var vm = Vm();
        _flasher.ProbeResult = new ProbeResult(ProbeKind.DifferentFirmware);
        vm.CheckFirmwareCommand.Execute(null);
        Until(() => vm.FirmwareMessage is not null);
        Assert.True(vm.CanUseFirmware);

        _rig.Config.Config.Connection.Type = ConnectionType.Udp;
        _rig.Config.RequestSave(connectionChanged: true);
        Until(() => vm.FirmwareMessage is null);
        Assert.False(vm.CanUseFirmware);
        Assert.False(vm.OfferInstall);
    }

    [Fact]
    public void LeavingThePage_DropsTheResult_AndFirmwareUsesTheConfiguredPort()
    {
        var vm = Vm();
        _flasher.ProbeResult = new ProbeResult(ProbeKind.DifferentFirmware);
        vm.CheckFirmwareCommand.Execute(null);
        Until(() => vm.FirmwareMessage is not null);
        vm.OnPageLeft();
        Assert.Null(vm.FirmwareMessage);

        _rig.Config.Config.Connection.Serial.ComPort = "COM8";
        _rig.Config.Save();
        vm.CheckFirmwareCommand.Execute(null);
        Until(() => vm.FirmwareMessage is not null);
        Assert.Equal("probe COM8", _flasher.Calls.Last());
    }
}
