using Volco.Core.Config;
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
    public static TrayStatus For(bool audioEngineFailed, LinkStatus link, ConnectionConfig connection)
    {
        var toolTip = audioEngineFailed ? "Volco — audio engine failed to start; see volco.log"
            : link.IsConnected ? $"Volco {BuildInfo.DisplayVersion} — Connected ({LinkText.TransportShort(connection)})"
            : $"Volco — {LinkText.Headline(link, connection)}";
        return new TrayStatus(link.IsDown, toolTip, link.State == LinkState.Disconnected ? "Connect" : "Disconnect");
    }
}
