using Volco.Core.Communication;
using Volco.Core.Updates;

namespace Volco.App;

/// <summary>
/// What the tray shows for the runtime's state: the red icon and "Disconnected" whenever the
/// controller is not connected, and the menu's Connect/Disconnect item.
/// </summary>
internal sealed record TrayStatus(bool Disconnected, string ToolTip, string ConnectionMenuText)
{
    public static TrayStatus For(bool audioEngineFailed, ConnectionStatus status, bool userDisconnected)
    {
        var disconnected = userDisconnected || status != ConnectionStatus.Connected;
        var toolTip = audioEngineFailed
            ? "Volco — audio engine failed to start; see volco.log"
            : disconnected ? "Volco — Disconnected" : $"Volco {BuildInfo.DisplayVersion}";
        return new TrayStatus(disconnected, toolTip, userDisconnected ? "Connect" : "Disconnect");
    }
}
