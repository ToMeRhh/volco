using Volco.Core.Communication;
using Xunit;

namespace Volco.App.Tests.Shell;

public class TrayStatusTests
{
    [Theory]
    [InlineData(ConnectionStatus.Disconnected, false)]
    [InlineData(ConnectionStatus.Error, false)]
    [InlineData(ConnectionStatus.Connected, true)]
    public void NotConnected_IsRedWithDisconnectedTooltip(ConnectionStatus status, bool userDisconnected)
    {
        var tray = TrayStatus.For(audioEngineFailed: false, status, userDisconnected);

        Assert.True(tray.Disconnected);
        Assert.Equal("Volco — Disconnected", tray.ToolTip);
    }

    [Fact]
    public void Connected_IsNormal_AndOffersDisconnect()
    {
        var tray = TrayStatus.For(audioEngineFailed: false, ConnectionStatus.Connected, userDisconnected: false);

        Assert.False(tray.Disconnected);
        Assert.StartsWith("Volco ", tray.ToolTip);
        Assert.DoesNotContain("Disconnected", tray.ToolTip);
        Assert.Equal("Disconnect", tray.ConnectionMenuText);
    }

    [Fact]
    public void UserDisconnected_OffersConnect()
        => Assert.Equal("Connect", TrayStatus.For(false, ConnectionStatus.Disconnected, true).ConnectionMenuText);

    [Fact]
    public void AudioEngineFailure_KeepsItsTooltip()
        => Assert.Contains("audio engine failed", TrayStatus.For(true, ConnectionStatus.Disconnected, false).ToolTip);
}
