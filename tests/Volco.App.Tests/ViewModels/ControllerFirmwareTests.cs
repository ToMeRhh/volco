using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Volco.App.ViewModels;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Firmware;
using Volco.Core.Runtime;
using Volco.Core.Tests.Firmware;
using Volco.Core.Tests.Runtime;
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
        Assert.Equal("This ESP32 doesn't have Volco's firmware yet.", vm.FirmwareMessage);
        Assert.Equal(vm.FirmwareMessage, vm.Failure);   // a warning: the card shows it
        Assert.True(vm.OfferInstall);                    // and its button is Install firmware
        Assert.True(vm.OfferFreshInstall);               // nothing answered as Volco: a first install
        Assert.Equal("Install", vm.InstallConfirmLabel);
        Assert.Equal("Install Volco's firmware on this ESP32? Anything else on it is replaced.", vm.InstallQuestion);
        Assert.False(vm.IsFirmwareRunning);
        Assert.Equal("probe COM3", _flasher.Calls.Single());
    }

    /// <summary>The three probe outcomes that offer an install: only a board that never answered as
    /// Volco this run gets the fresh-install wording; another build and a silent Volco firmware get
    /// the reinstall's, which erases the stored setup.</summary>
    [Fact]
    public void Check_AfterAVolcoHello_IsADifferentBuild_AndAReinstall()
    {
        var vm = Vm();
        _rig.Connection!.SetStatus(TransportStatus.Open);
        _rig.Connection.RaiseMessage(Hello("0000aaaa1111"));
        _ui.RunAll();
        _flasher.ProbeResult = new ProbeResult(ProbeKind.DifferentFirmware);
        vm.CheckFirmwareCommand.Execute(null);
        Until(() => vm.FirmwareMessage is not null);
        Assert.Equal("This ESP32 runs a different build of Volco's firmware.", vm.FirmwareMessage);
        Assert.True(vm.OfferInstall);
        Assert.False(vm.OfferFreshInstall);
        Assert.Equal("Reinstall", vm.InstallConfirmLabel);
        Assert.StartsWith("Reinstalling erases the setup", vm.InstallQuestion);
    }

    [Fact]
    public void Check_FindingSilentVolcoFirmware_IsAReinstall()
    {
        var vm = Vm();
        _flasher.ProbeResult = new ProbeResult(ProbeKind.VolcoMatches);
        vm.CheckFirmwareCommand.Execute(null);
        Until(() => _flasher.Calls.Count == 1 && !_rig.Runtime.IsSuspended);
        Thread.Sleep(50);
        _time.Advance(FirmwareService.HelloWait);
        Until(() => vm.FirmwareMessage is not null);
        Assert.Equal("Volco's firmware is installed but didn't answer. Reinstall it?", vm.FirmwareMessage);
        Assert.True(vm.OfferInstall);
        Assert.False(vm.OfferFreshInstall);
        Assert.Equal("Reinstall", vm.InstallConfirmLabel);
        Assert.StartsWith("Reinstalling erases the setup", vm.InstallQuestion);
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
        Assert.Equal("This ESP32 doesn't have Volco's firmware yet.", vm.FirmwareMessage);

        vm.CancelInstallCommand.Execute(null);
        Assert.False(vm.ConfirmInstallPending);
        Assert.Equal("This ESP32 doesn't have Volco's firmware yet.", vm.FirmwareMessage);
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
    public void HidingTheWindow_DropsTheResult_AndFirmwareUsesTheConfiguredPort()
    {
        var vm = Vm();
        _flasher.ProbeResult = new ProbeResult(ProbeKind.DifferentFirmware);
        vm.CheckFirmwareCommand.Execute(null);
        Until(() => vm.FirmwareMessage is not null);
        vm.OnPageLeft();   // the card is on every page: the result stays
        Assert.NotNull(vm.FirmwareMessage);
        vm.OnWindowHidden();
        Assert.Null(vm.FirmwareMessage);
        Assert.False(vm.OfferInstall);

        _rig.Config.Config.Connection.Serial.ComPort = "COM8";
        _rig.Config.Save();
        vm.CheckFirmwareCommand.Execute(null);
        Until(() => vm.FirmwareMessage is not null);
        Assert.Equal("probe COM8", _flasher.Calls.Last());
    }

    // ── Under Automatic (hardware-page spec §10.2) ──────────────────────────────────────────────

    private sealed class Ports(params SerialPortDescription[] ports) : ISerialPortInfo
    {
        public IReadOnlyList<SerialPortDescription> List() => ports;
    }

    private TestRig AutomaticRig(params SerialPortDescription[] ports)
    {
        var options = new VolcoRuntimeOptions
        {
            Link = new ControllerLinkOptions(TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.FromHours(1)),
            Firmware = Catalog,
            Time = _time,
            ConnectGrace = TimeSpan.Zero,
            SerialFactory = name => new FakeControllerConnection(new ConnectionConfig { Serial = { ComPort = name } })
            { StatusOnConnect = TransportStatus.Open },
        };
        return new TestRig(options: options, serialPorts: new Ports(ports), realTransports: true);
    }

    private ControllerViewModel AutomaticVm(TestRig rig)
    {
        _autoFlasher = new FakeFlasher(() => rig.Runtime.IsSuspended);
        return _vm = new ControllerViewModel(rig.Config, rig.Runtime, _ui, new FirmwareService(rig.Runtime, _autoFlasher, Catalog, _time));
    }

    private FakeFlasher? _autoFlasher;

    [Fact]
    public void CheckUsesFoundPort()
    {
        using var rig = AutomaticRig(new SerialPortDescription("COM7", "USB-SERIAL CH340", 0x1A86, 0x7523));
        for (int i = 0; i < 7; i++) _time.Advance(TimeSpan.FromSeconds(1));   // the round ends on the only candidate
        var vm = AutomaticVm(rig);
        _autoFlasher!.ProbeResult = new ProbeResult(ProbeKind.DifferentFirmware);
        vm.CheckFirmwareCommand.Execute(null);
        Until(() => vm.FirmwareMessage is not null);
        Assert.False(vm.NeedsFirmwarePort);
        Assert.Equal("probe COM7", _autoFlasher.Calls.Single());
    }

    [Fact]
    public void CheckAsksPortWhenSeveralWithoutCh340()
    {
        using var rig = AutomaticRig(new SerialPortDescription("COM1", "", 0, 0), new SerialPortDescription("COM2", "Some adapter", 0x1234, 0x5678));
        var vm = AutomaticVm(rig);
        _autoFlasher!.ProbeResult = new ProbeResult(ProbeKind.DifferentFirmware);
        Assert.Null(rig.Runtime.UsbPort);
        vm.CheckFirmwareCommand.Execute(null);
        _ui.RunAll();
        Assert.True(vm.NeedsFirmwarePort);
        Assert.False(vm.IsFirmwareRunning);
        Assert.Empty(_autoFlasher.Calls);   // nothing starts before a choice
        Assert.Equal(new[] { "COM1", "COM2 · Some adapter" }, vm.FirmwarePortChoices.Select(c => c.Label));
        Assert.All(vm.FirmwarePortChoices, c => Assert.NotNull(c.Port));

        vm.ChooseFirmwarePortCommand.Execute(vm.FirmwarePortChoices[1]);
        Until(() => vm.FirmwareMessage is not null);
        Assert.False(vm.NeedsFirmwarePort);
        Assert.Equal("probe COM2", _autoFlasher.Calls.Single());
    }

    [Fact]
    public void CancellingThePortQuestionRunsNothing()
    {
        using var rig = AutomaticRig(new SerialPortDescription("COM1", "", 0, 0), new SerialPortDescription("COM2", "", 0, 0));
        var vm = AutomaticVm(rig);
        vm.CheckFirmwareCommand.Execute(null);
        Assert.True(vm.NeedsFirmwarePort);
        vm.CancelFirmwarePort();
        Assert.False(vm.NeedsFirmwarePort);
        Assert.Empty(_autoFlasher!.Calls);
    }

    [Fact]
    public void InstallSetsLastPortNotComPort()
    {
        using var rig = AutomaticRig(new SerialPortDescription("COM1", "", 0, 0), new SerialPortDescription("COM2", "", 0, 0));
        var vm = AutomaticVm(rig);
        vm.InstallFirmwareCommand.Execute(null);
        vm.ConfirmInstallCommand.Execute(null);
        Assert.True(vm.NeedsFirmwarePort);
        vm.ChooseFirmwarePortCommand.Execute(vm.FirmwarePortChoices[1]);
        Until(() => rig.Config.Config.Connection.Serial.LastPort == "COM2" && _autoFlasher!.Calls.Count == 1);   // the Hello after the flash isn't needed here
        Assert.Equal("flash COM2 Install", _autoFlasher!.Calls.Single());
        Assert.Equal("COM2", rig.Config.Config.Connection.Serial.LastPort);
        Assert.Equal("", rig.Config.Config.Connection.Serial.ComPort);
    }

    [Fact]
    public void ProgressDialogAsksThePort_AndCloseAbandonsIt()
    {
        using var rig = AutomaticRig(new SerialPortDescription("COM1", "", 0, 0), new SerialPortDescription("COM2", "", 0, 0));
        var vm = AutomaticVm(rig);
        using var dialog = new Volco.App.ViewModels.Hardware.ProgressDialogViewModel(vm, () => { });
        vm.CheckFirmwareCommand.Execute(null);
        Assert.True(dialog.IsOpen);
        Assert.True(dialog.IsAskingPort);
        Assert.False(dialog.IsDone);
        Assert.Equal("Which port is the controller on?", dialog.Title);
        Assert.Equal(2, dialog.PortChoices.Count);

        dialog.CloseCommand.Execute(null);
        Assert.False(dialog.IsOpen);
        Assert.False(dialog.IsAskingPort);
        Assert.False(vm.NeedsFirmwarePort);
        Assert.Empty(_autoFlasher!.Calls);
    }

    [Fact]
    public void NoSerialPorts_EndsWithAVisibleResult()
    {
        using var rig = AutomaticRig();
        var vm = AutomaticVm(rig);
        using var dialog = new Volco.App.ViewModels.Hardware.ProgressDialogViewModel(vm, () => { });
        vm.CheckFirmwareCommand.Execute(null);
        Assert.False(vm.NeedsFirmwarePort);
        Assert.Equal("No USB port found. Plug the controller in.", vm.FirmwareMessage);
        Assert.Equal(LinkTone.Warning, vm.FirmwareMessageTone);
        Assert.True(dialog.IsOpen);
        Assert.True(dialog.IsDone);
        Assert.Equal("No USB port found. Plug the controller in.", dialog.ResultText);
        Assert.Empty(_autoFlasher!.Calls);
    }

    [Fact]
    public void ConnectionChangingWhileAsking_CancelsTheQuestion()
    {
        using var rig = AutomaticRig(new SerialPortDescription("COM1", "", 0, 0), new SerialPortDescription("COM2", "", 0, 0));
        var vm = AutomaticVm(rig);
        using var dialog = new Volco.App.ViewModels.Hardware.ProgressDialogViewModel(vm, () => { });
        vm.CheckFirmwareCommand.Execute(null);
        Assert.True(dialog.IsAskingPort);
        var choice = vm.FirmwarePortChoices[0];

        rig.Config.Config.Connection.Type = ConnectionType.WebSocket;
        rig.Config.RequestSave(connectionChanged: true);
        Until(() => !vm.NeedsFirmwarePort);
        Assert.False(dialog.IsOpen);
        Assert.False(dialog.IsAskingPort);
        vm.ChooseFirmwarePortCommand.Execute(choice);   // stale: nothing runs
        Assert.Empty(_autoFlasher!.Calls);
    }

    [Fact]
    public void TestStartingWhileAsking_CancelsTheQuestion()
    {
        using var rig = AutomaticRig(new SerialPortDescription("COM1", "", 0, 0), new SerialPortDescription("COM2", "", 0, 0));
        var vm = AutomaticVm(rig);
        using var dialog = new Volco.App.ViewModels.Hardware.ProgressDialogViewModel(vm, () => { });
        vm.CheckFirmwareCommand.Execute(null);
        Assert.True(dialog.IsAskingPort);
        var choice = vm.FirmwarePortChoices[0];

        vm.IsTesting = true;   // a test (or a save) took over: the page can't run firmware now
        Assert.False(vm.NeedsFirmwarePort);
        Assert.False(dialog.IsAskingPort);
        vm.ChooseFirmwarePortCommand.Execute(choice);
        Assert.Empty(_autoFlasher!.Calls);
    }
}
