using Microsoft.Extensions.Time.Testing;
using Volco.App.ViewModels;
using Volco.App.ViewModels.Hardware;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Runtime;
using Volco.Core.Tests.Runtime;
using Xunit;

namespace Volco.App.Tests.ViewModels;

/// <summary>The link status the sidebar footer, the tray and the device card share
/// (<see cref="ConnectionViewModel"/>), and its Connect, Reconnect and Disconnect.</summary>
public sealed class ConnectionViewModelTests : IDisposable
{
    private readonly TestRig _rig = new();
    private readonly RecordingDispatcher _ui = new();
    private readonly SettingsViewModel _vm;

    public ConnectionViewModelTests() => _vm = _rig.CreateSettings(_ui);

    public void Dispose()
    {
        _vm.Dispose();
        _rig.Dispose();
    }

    private ConnectionViewModel Connection => _vm.Connection;

    [Fact]
    public void HardwareSummary_AndVersionMismatch()
    {
        _ = Connection;
        _rig.Connection!.SetStatus(TransportStatus.Open);
        _rig.Connection.RaiseMessage(new HelloMessage(ProtocolVersion.Current, 5,
            new[] { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair }));
        _ui.RunAll();
        Assert.Equal("5 sliders · 3 buttons (2 LED, 1 LED pair)", Connection.HardwareSummary);
        Assert.False(Connection.HasStatusExplanation);

        // Another protocol version: not connected, and amber rather than red.
        _rig.Connection.RaiseMessage(new HelloMessage(9, 5, new[] { IndicatorKind.Led }));
        _ui.RunAll();
        Assert.Equal("Firmware update needed", Connection.StatusText);
        Assert.StartsWith("The controller's firmware is from another version of Volco.", Connection.StatusExplanation);
        Assert.Equal(LinkTone.Warning, Connection.StatusTone);
        Assert.False(Connection.HasHardwareSummary);
    }

    [Fact]
    public void TheHardwareSummary_ShowsOnEveryTransport()
    {
        _rig.Config.Config.Connection.Type = ConnectionType.Udp;
        _rig.Config.Save();
        _ = Connection;
        _rig.Runtime.Reconnect();   // a UDP transport now

        _rig.Connection!.SetStatus(TransportStatus.Open);
        _rig.Connection.RaiseMessage(new HelloMessage(ProtocolVersion.Current, 2, new[] { IndicatorKind.Screen }));
        _ui.RunAll();
        Assert.Equal("Connected", Connection.StatusText);
        Assert.Equal("2 sliders · 1 button (1 screen)", Connection.HardwareSummary);
    }

    [Fact]
    public void ABusyPort_IsRed_AndSaysWhatToDo()
    {
        _rig.Config.Config.Connection.Serial.ComPort = "COM3";
        _ = Connection;
        _rig.Connection!.SetStatus(TransportStatus.Open);
        _ui.RunAll();
        Assert.Equal("Connecting…", Connection.StatusText);
        Assert.Equal(LinkTone.Neutral, Connection.StatusTone);

        _rig.Connection.SetStatus(TransportStatus.Failed);
        _ui.RunAll();
        Assert.Equal("Can't use COM3", Connection.StatusText);
        Assert.StartsWith("Another app is using COM3", Connection.StatusExplanation);
        Assert.Equal(LinkTone.Danger, Connection.StatusTone);
    }

    [Fact]
    public void Reconnect_StartsAFreshTransport()
    {
        var first = _rig.Connection;
        Connection.ReconnectCommand.Execute(null);
        Assert.NotSame(first, _rig.Connection);
    }

    [Fact]
    public void Disconnect_ThenConnect()
    {
        _rig.Connection!.SetStatus(TransportStatus.Open);
        _ui.RunAll();
        Assert.False(Connection.IsUserDisconnected);

        Connection.DisconnectCommand.Execute(null);
        _ui.RunAll();
        Assert.Equal(1, _rig.Connection.DisposeCount);
        Assert.Equal("Disconnected", Connection.StatusText);
        Assert.StartsWith("You disconnected Volco.", Connection.StatusExplanation);
        Assert.True(Connection.IsUserDisconnected);

        var old = _rig.Connection;
        Connection.ConnectCommand.Execute(null);
        _ui.RunAll();
        Assert.NotSame(old, _rig.Connection);
        Assert.Equal("Looking for your controller", Connection.StatusText);
        Assert.False(Connection.IsUserDisconnected);
    }

    private sealed class Ports(params SerialPortDescription[] ports) : ISerialPortInfo
    {
        public IReadOnlyList<SerialPortDescription> List() => ports;
    }

    [Fact]
    public void AutomaticFirstWithFoundPort()
    {
        var time = new FakeTimeProvider();
        var options = new VolcoRuntimeOptions
        {
            Time = time,
            ConnectGrace = TimeSpan.Zero,
            SerialFactory = name => new FakeControllerConnection(new ConnectionConfig { Serial = { ComPort = name } })
            { StatusOnConnect = TransportStatus.Open },
        };
        using var rig = new TestRig(options: options, serialPorts: new Ports(new SerialPortDescription("COM7", "USB-SERIAL CH340", 0x1A86, 0x7523)),
                                    realTransports: true);
        var ui = new RecordingDispatcher();
        using var vm = rig.CreateSettings(ui);
        Assert.Equal("Automatic", vm.Connection.PortChoices[0].Label);

        for (int i = 0; i < 7; i++) time.Advance(TimeSpan.FromSeconds(1));   // the round ends on the only candidate
        ui.RunAll();

        Assert.Equal("COM7", vm.Connection.UsbPort);
        Assert.Equal("Automatic (COM7)", vm.Connection.PortChoices[0].Label);
        Assert.Null(vm.Connection.PortChoices[0].Port);
        Assert.Same(vm.Connection.PortChoices[0], vm.Connection.SelectedPort);
    }

    [Fact]
    public void DescriptionsWithSeam()
    {
        using var rig = new TestRig(serialPorts: new Ports(new SerialPortDescription("COM3", "USB-SERIAL CH340", 0x1A86, 0x7523)));
        using var vm = rig.CreateSettings(new RecordingDispatcher());
        Assert.Equal("COM3 · USB-SERIAL CH340", vm.Connection.PortChoices.Single(p => p.Port == "COM3").Label);
    }

    [Fact]
    public void NamesOnlyWithoutSeam()
    {
        using var rig = new TestRig();
        rig.Config.Config.Connection.Serial.ComPort = "COM3";
        using var vm = rig.CreateSettings(new RecordingDispatcher());
        Assert.Equal("COM3", vm.Connection.PortChoices.Single(p => p.Port == "COM3").Label);
    }

    [Fact]
    public void SelectingAutomaticClearsComPort()
    {
        using var rig = new TestRig(serialPorts: new Ports(new SerialPortDescription("COM3", "USB Serial Device", 0x10C4, 0xEA60)));
        rig.Config.Config.Connection.Serial.ComPort = "COM3";
        using var vm = rig.CreateSettings(new RecordingDispatcher());
        Assert.Equal("COM3", vm.Connection.SelectedPort!.Port);

        vm.Connection.SelectedPort = vm.Connection.PortChoices[0];

        Assert.Equal("", rig.Config.Config.Connection.Serial.ComPort);
        Assert.Equal("", vm.Connection.ComPort);
        Assert.Null(vm.Connection.SelectedPort!.Port);
        rig.Config.FlushPendingSave();

        // The ComboBox clearing the list during a refresh is no choice.
        vm.Connection.SelectedPort = null;
        Assert.Equal("", rig.Config.Config.Connection.Serial.ComPort);
    }

    [Fact]
    public void HeadlineNamesFoundPort()
    {
        FakeControllerConnection? opened = null;
        var options = TestRig.NoGrace with
        {
            SerialFactory = name => opened = new FakeControllerConnection(new ConnectionConfig { Serial = { ComPort = name } })
            { StatusOnConnect = TransportStatus.Open },
        };
        using var rig = new TestRig(options: options, serialPorts: new Ports(new SerialPortDescription("COM3", "USB-SERIAL CH340", 0x1A86, 0x7523)),
                                    realTransports: true);
        var ui = new RecordingDispatcher();
        using var vm = rig.CreateSettings(ui);

        opened!.RaiseMessage(new HelloMessage(ProtocolVersion.Current, 2, new[] { IndicatorKind.Led }));
        ui.RunAll();

        Assert.Equal("Connected", vm.Connection.StatusText);
        // The banner (not set up yet) is the controller's business; the port the status VM feeds the card is the found one.
        var inputs = vm.Hardware.Status.Inputs() with { Banner = Volco.Core.Controller.ControllerBanner.Empty };
        Assert.Equal("Connected over USB (COM3)", DeviceCard.From(inputs).Status);
    }

    [Fact]
    public void NoAnswerWithoutPort()
    {
        var link = new Volco.Core.Runtime.LinkStatus(Volco.Core.Runtime.LinkState.NoAnswer);
        var c = new ConnectionConfig { Type = ConnectionType.Serial };
        Assert.Equal("No controller answers", LinkText.Headline(link, c, null));
        Assert.Equal("Something is plugged in, but nothing answers as a Volco controller. It may not have Volco's firmware yet: check it on the Controller page.",
                     LinkText.Explanation(link, c, null));
        Assert.Equal("USB cable · looking for the controller", LinkText.Transport(c, null, null));
        Assert.Equal("COM3 · USB cable", LinkText.Transport(c, null, "COM3"));
    }
}
