using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Runtime;
using Volco.Core.Updates;

namespace Volco.App;

/// <summary>
/// What the tray shows for the link: the red icon whenever the controller isn't connected
/// (<see cref="LinkStatus.IsDown"/>: an expected wait such as a firmware update or a restart after
/// a save is not), a tooltip that says why in the UI's own words, and the menu's Connect/Disconnect.
/// </summary>
internal sealed record TrayStatus(bool Down, string ToolTip, string ConnectionMenuText)
{
    /// <param name="banner">The controller's §8.3 banner: while it needs the user, the tooltip says
    /// so on a second line, naming the "Volco controller".</param>
    public static TrayStatus For(bool audioEngineFailed, LinkStatus link, ConnectionConfig connection, ControllerBanner? banner = null)
    {
        var toolTip = audioEngineFailed ? "Volco — audio engine failed to start; see volco.log"
            : link.IsConnected ? $"Volco {BuildInfo.DisplayVersion} — Connected ({LinkText.TransportShort(connection)})"
            : $"Volco — {LinkText.Headline(link, connection)}";
        if (ControllerLine(banner) is { } line) toolTip += "\n" + line;
        return new TrayStatus(link.IsDown, toolTip, link.State == LinkState.Disconnected ? "Connect" : "Disconnect");
    }

    /// <summary>The tooltip's controller line; none where the headline already says it ("Controller
    /// not responding", "Firmware update needed") or the controller needs nothing.</summary>
    private static string? ControllerLine(ControllerBanner? banner)
    {
        if (banner is not { NeedsAttention: true } b || b.Kind is BannerKind.NoAnswer or BannerKind.Incompatible) return null;
        return b.FirmwareUpdateAvailable ? "Volco controller firmware update available"
            : b.Kind is BannerKind.NotSetUp or BannerKind.NotSetUpHasMirror ? "The Volco controller needs setting up"
            : "The Volco controller needs attention";
    }
}
