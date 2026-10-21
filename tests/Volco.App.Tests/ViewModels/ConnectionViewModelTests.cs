using Volco.App.ViewModels;
using Volco.Core.Communication;
using Volco.Core.Config;
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
}
