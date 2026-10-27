using Volco.Core.Config;
using Volco.Core.Runtime;

namespace Volco.App;

/// <summary>How a status reads: the dot's and the explanation's colour. Accent marks a next step
/// that is no problem (a controller not set up yet).</summary>
public enum LinkTone { Neutral, Good, Warning, Danger, Accent }

/// <summary>
/// Every word the UI says about the link (spec: 2026-10-06 link state), so the tray, the sidebar
/// card and the Controller page agree. "Connected" is only ever said of <see cref="LinkState.Connected"/>
/// and "Disconnected" only of the user's Disconnect; the physical layer is described with
/// "plugged in", "port", "listening" and "answering", on the detail lines.
/// </summary>
public static class LinkText
{
    /// <summary>The status line: "Connected", "Controller not responding", …</summary>
    /// <param name="usbPort">The USB port in use (<c>VolcoRuntime.UsbPort</c>): the chosen one, or the one Automatic found; null for none.</param>
    public static string Headline(LinkStatus link, ConnectionConfig c, string? usbPort) => link.State switch
    {
        LinkState.Connected => "Connected",
        LinkState.Disconnected => "Disconnected",
        LinkState.Searching => "Looking for your controller",
        LinkState.PortBusy => c.Type == ConnectionType.Serial
            ? usbPort is null ? "Can't use the USB port" : $"Can't use {usbPort}"
            : $"Can't use port {Port(c)}",
        LinkState.NoAnswer => c.Type == ConnectionType.Serial && usbPort is null ? "No controller answers" : "Controller not responding",
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
    public static string Transport(ConnectionConfig c, string? peer, string? usbPort) => c.Type switch
    {
        ConnectionType.Serial => usbPort is null ? "USB cable · looking for the controller" : $"{usbPort} · USB cable",
        ConnectionType.Udp => peer is null ? $"Wi-Fi (UDP) · port {c.Udp.ListenPort}" : $"{peer} · Wi-Fi",
        _ => $"WebSocket · port {c.WebSocket.Port}",
    };

    /// <summary>The tray's short form of the transport: "COM3", "Wi-Fi", "WebSocket".</summary>
    public static string TransportShort(ConnectionConfig c, string? usbPort) => c.Type switch
    {
        ConnectionType.Serial => usbPort ?? "USB",
        ConnectionType.Udp => "Wi-Fi",
        _ => "WebSocket",
    };

    /// <summary>What a status that isn't Connected means, and what to do; "" when connected.</summary>
    public static string Explanation(LinkStatus link, ConnectionConfig c, string? usbPort)
    {
        var serial = c.Type == ConnectionType.Serial;
        var port = usbPort;
        return link.State switch
        {
            LinkState.Disconnected => serial
                ? $"You disconnected Volco. {port ?? "The port"} is free for other apps."
                : $"You disconnected Volco. It isn't listening on port {Port(c)}.",
            LinkState.Searching => serial
                ? string.IsNullOrEmpty(c.Serial.ComPort) ? "Plug the controller in with USB." : $"{c.Serial.ComPort} isn't there. Plug the controller in with USB."
                : $"Listening on port {Port(c)}. No controller has sent anything yet.",
            LinkState.PortBusy => serial
                ? port is null ? "Another app is using the USB port (a serial monitor?). Close it and Volco carries on by itself."
                    : $"Another app is using {port} (a serial monitor?). Close it and Volco carries on by itself."
                : $"Another app is using port {Port(c)}. Choose another one, here and on the controller.",
            LinkState.NoAnswer => serial && port is null
                ? "Something is plugged in, but nothing answers as a Volco controller. " +
                  "It may not have Volco's firmware yet: check it on the Controller page."
                : $"Something is plugged in on {port}, but it isn't answering as a Volco controller. " +
                  "It may not have Volco's firmware yet: check it on the Controller page.",
            LinkState.Incompatible => serial
                ? "The controller's firmware is from another version of Volco. Update it on the Controller page."
                : "The controller's firmware is from another version of Volco. Plug it in with USB to update it on the Controller page.",
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
