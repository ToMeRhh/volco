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

    /// <summary>The tooltip with the settings file's line below it, while changes are unsaved or a
    /// save keeps failing (<see cref="SaveText.TrayLine"/>).</summary>
    public static string WithSaveLine(string toolTip, ConfigSaveState save)
        => SaveText.TrayLine(save) is { Length: > 0 } line ? $"{toolTip}\n{line}" : toolTip;

    /// <summary>A failing save asks for the user like a controller that needs them: the attention icon.</summary>
    public static bool SaveNeedsAttention(ConfigSaveState save) => save == ConfigSaveState.Failed;
}
