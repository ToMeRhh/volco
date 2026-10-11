using Volco.Core.Config;
using Volco.Core.Runtime;

namespace Volco.App;

/// <summary>How a status reads: the dot's and the explanation's colour.</summary>
public enum LinkTone { Neutral, Good, Warning, Danger }

/// <summary>
/// Every word the UI says about the link (spec: 2026-10-06 link state), so the tray, the sidebar
/// footer and both pages agree. "Connected" is only ever said of <see cref="LinkState.Connected"/>
/// and "Disconnected" only of the user's Disconnect; the physical layer is described with
/// "plugged in", "port", "listening" and "answering", on the detail lines.
/// </summary>
public static class LinkText
{
    /// <summary>The status line: "Connected", "Controller not responding", …</summary>
    public static string Headline(LinkStatus link, ConnectionConfig c) => link.State switch
    {
        LinkState.Connected => "Connected",
        LinkState.Disconnected => "Disconnected",
        LinkState.Searching => "Looking for your controller",
        LinkState.PortBusy => c.Type == ConnectionType.Serial
            ? string.IsNullOrEmpty(c.Serial.ComPort) ? "No port chosen" : $"Can't use {c.Serial.ComPort}"
            : $"Can't use port {Port(c)}",
        LinkState.NoAnswer => "Controller not responding",
        LinkState.Incompatible => "Firmware update needed",
        _ => Activity(link.Activity),
    };

    public static string Activity(LinkActivity activity) => activity switch
    {
        LinkActivity.CheckingFirmware => "Checking firmware…",
        LinkActivity.InstallingFirmware => "Installing firmware…",
        LinkActivity.UpdatingFirmware => "Updating firmware…",
        LinkActivity.Restarting => "Controller restarting…",
        _ => "Connecting…",
    };

    /// <summary>The transport, briefly: "COM3 · USB cable", "192.168.1.40 · Wi-Fi", "WebSocket · port 8080".</summary>
    public static string Transport(ConnectionConfig c, string? peer) => c.Type switch
    {
        ConnectionType.Serial => string.IsNullOrEmpty(c.Serial.ComPort) ? "USB cable · no port chosen" : $"{c.Serial.ComPort} · USB cable",
        ConnectionType.Udp => peer is null ? $"Wi-Fi (UDP) · port {c.Udp.ListenPort}" : $"{peer} · Wi-Fi",
        _ => $"WebSocket · port {c.WebSocket.Port}",
    };

    /// <summary>The footer's short form of the transport: "COM3", "Wi-Fi", "WebSocket".</summary>
    public static string TransportShort(ConnectionConfig c) => c.Type switch
    {
        ConnectionType.Serial => string.IsNullOrEmpty(c.Serial.ComPort) ? "USB" : c.Serial.ComPort,
        ConnectionType.Udp => "Wi-Fi",
        _ => "WebSocket",
    };

    /// <summary>The sidebar footer: "Connected · COM3", or the headline.</summary>
    public static string Footer(LinkStatus link, ConnectionConfig c)
        => link.IsConnected ? $"Connected · {TransportShort(c)}" : Headline(link, c);

    /// <summary>What a status that isn't Connected means, and what to do; "" when connected.</summary>
    public static string Explanation(LinkStatus link, ConnectionConfig c)
    {
        var serial = c.Type == ConnectionType.Serial;
        var port = c.Serial.ComPort;
        return link.State switch
        {
            LinkState.Disconnected => serial
                ? $"You disconnected Volco. {(string.IsNullOrEmpty(port) ? "The port" : port)} is free for other apps."
                : $"You disconnected Volco. It isn't listening on port {Port(c)}.",
            LinkState.Searching => serial
                ? string.IsNullOrEmpty(port) ? "Choose the controller's port below." : $"{port} isn't there. Plug the controller in with USB."
                : $"Listening on port {Port(c)}. No controller has sent anything yet.",
            LinkState.PortBusy => serial
                ? string.IsNullOrEmpty(port) ? "Choose the controller's port below."
                    : $"Another app is using {port} (a serial monitor?). Close it and Volco carries on by itself."
                : $"Another app is using port {Port(c)}. Choose another one, here and on the controller.",
            LinkState.NoAnswer => $"Something is plugged in on {port}, but it isn't answering as a Volco controller. " +
                                  "It may not have Volco's firmware yet: check it on the Hardware page.",
            LinkState.Incompatible => serial
                ? "The controller's firmware is from another version of Volco. Update it on the Hardware page."
                : "The controller's firmware is from another version of Volco. Plug it in with USB to update it on the Hardware page.",
            LinkState.Waiting => link.Activity switch
            {
                LinkActivity.CheckingFirmware or LinkActivity.InstallingFirmware or LinkActivity.UpdatingFirmware
                    => "Volco is using the USB port for the firmware. The controller restarts afterwards.",
                LinkActivity.Restarting => "The controller is restarting with its new setup.",
                _ => "Waiting for the controller to answer.",
            },
            _ => "",
        };
    }

    /// <summary>The dot: green when connected, amber when the firmware needs an update, red when
    /// something is wrong, grey for the rest (a wait, a search, the user's Disconnect).</summary>
    public static LinkTone Tone(LinkState state) => state switch
    {
        LinkState.Connected => LinkTone.Good,
        LinkState.Incompatible => LinkTone.Warning,
        LinkState.PortBusy or LinkState.NoAnswer => LinkTone.Danger,
        _ => LinkTone.Neutral,
    };

    private static int Port(ConnectionConfig c) => c.Type == ConnectionType.Udp ? c.Udp.ListenPort : c.WebSocket.Port;
}
