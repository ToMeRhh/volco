using Volco.Core.Config;
using Volco.Core.Runtime;
using Xunit;

namespace Volco.App.Tests.Shell;

public class TrayStatusTests
{
    private static readonly ConnectionConfig Usb = new() { Type = ConnectionType.Serial, Serial = { ComPort = "COM3" } };

    [Theory]
    [InlineData(LinkState.Disconnected, "Volco — Disconnected")]
    [InlineData(LinkState.Searching, "Volco — Looking for your controller")]
    [InlineData(LinkState.PortBusy, "Volco — Can't use COM3")]
    [InlineData(LinkState.NoAnswer, "Volco — Controller not responding")]
    [InlineData(LinkState.Incompatible, "Volco — Firmware update needed")]
    public void NotConnected_IsRed_AndSaysWhy(LinkState state, string toolTip)
    {
        var tray = TrayStatus.For(audioEngineFailed: false, new LinkStatus(state), Usb);

        Assert.True(tray.Down);
        Assert.Equal(toolTip, tray.ToolTip);
    }

    [Theory]
    [InlineData(LinkActivity.Connecting, "Volco — Connecting…")]
    [InlineData(LinkActivity.UpdatingFirmware, "Volco — Updating firmware…")]
    [InlineData(LinkActivity.Restarting, "Volco — Controller restarting…")]
    public void AnExpectedWait_IsNotRed(LinkActivity activity, string toolTip)
    {
        var tray = TrayStatus.For(audioEngineFailed: false, new LinkStatus(LinkState.Waiting, activity), Usb);

        Assert.False(tray.Down);
        Assert.Equal(toolTip, tray.ToolTip);
        Assert.Equal("Disconnect", tray.ConnectionMenuText);
    }

    [Fact]
    public void Connected_IsNormal_AndOffersDisconnect()
    {
        var tray = TrayStatus.For(audioEngineFailed: false, new LinkStatus(LinkState.Connected), Usb);

        Assert.False(tray.Down);
        Assert.StartsWith("Volco ", tray.ToolTip);
        Assert.EndsWith("Connected (COM3)", tray.ToolTip);
        Assert.Equal("Disconnect", tray.ConnectionMenuText);
    }

    [Fact]
    public void UserDisconnected_OffersConnect()
        => Assert.Equal("Connect", TrayStatus.For(false, LinkStatus.Disconnected, Usb).ConnectionMenuText);

    [Fact]
    public void AudioEngineFailure_KeepsItsTooltip()
        => Assert.Contains("audio engine failed", TrayStatus.For(true, new LinkStatus(LinkState.Searching), Usb).ToolTip);
}
