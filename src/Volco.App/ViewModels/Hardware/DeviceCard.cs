using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Runtime;

namespace Volco.App.ViewModels.Hardware;

/// <summary>The device card's one primary button (hardware-page spec §4.1).</summary>
public enum CardAction { None, Connect, Retry, CheckFirmware, Update, FixIt, Compare, TestNow }

/// <param name="Link">The runtime's link status, as the sidebar and the tray read it.</param>
/// <param name="Banner">The §8.3 banner and the firmware notice.</param>
/// <param name="Transport">How Volco talks to the controller.</param>
/// <param name="Port">The serial port (USB), or the UDP or WebSocket port number; null when none is chosen.</param>
/// <param name="FirmwareStage">A firmware operation runs: "Updating firmware…", "Installing firmware…"
/// or "Checking firmware…"; null otherwise.</param>
/// <param name="FirmwareProgress">The firmware write's fraction (0–1); null when it has none (a check).</param>
/// <param name="SavePhase">A config save runs: "Saving to the controller…", then "Restarting…"; null otherwise.</param>
/// <param name="Failure">The last operation's error message (save, install, update or check), kept
/// until the next action; null when there is none.</param>
/// <param name="OfferTest">A firmware update or a first save just finished: the card offers a test.</param>
public sealed record CardInputs(LinkStatus Link, ControllerBanner Banner, ConnectionType Transport, string? Port,
                                string? FirmwareStage, double? FirmwareProgress, string? SavePhase, string? Failure,
                                bool OfferTest);

/// <param name="Status">The status line, next to the coloured dot.</param>
/// <param name="Detail">The one detail line; null for none.</param>
/// <param name="Progress">The progress bar's fraction (0–1); null for no bar.</param>
/// <param name="StatusOpensConnection">The status line opens the Connection dialog (the ›).</param>
public sealed record CardState(string Status, LinkTone Tone, string? Detail, CardAction Action, double? Progress,
                               bool StatusOpensConnection);

/// <summary>The hardware-page spec §4.1 table as a pure function: the first matching row wins.</summary>
public static class DeviceCard
{
    public const string Name = "Volco controller";

    public static CardState From(CardInputs i)
    {
        var state = Row(i);
        // A running operation and the user's Disconnect keep their own detail line.
        bool keepsDetail = i.FirmwareStage is not null || i.SavePhase is not null || i.Banner.Kind == BannerKind.Saving
                           || i.Link.State == LinkState.Disconnected;
        return i.Failure is { } failure && !keepsDetail ? state with { Detail = failure } : state;
    }

    private static CardState Row(CardInputs i)
    {
        var link = i.Link;
        var banner = i.Banner;
        bool serial = i.Transport == ConnectionType.Serial;

        if (i.FirmwareStage is { } stage)
            return new(i.FirmwareProgress is { } p ? $"{stage} {Math.Round(p * 100):0}%" : stage, LinkTone.Neutral,
                       "Don't unplug the controller.", CardAction.None, i.FirmwareProgress, false);
        if (i.SavePhase is { } phase)
            return new(phase, LinkTone.Neutral, null, CardAction.None, null, false);
        if (banner.Kind == BannerKind.Saving)
            return new("Saving to the controller…", LinkTone.Neutral, null, CardAction.None, null, false);

        switch (link.State)
        {
            case LinkState.Disconnected:
                return new("Disconnected", LinkTone.Neutral, "You disconnected Volco from the controller.", CardAction.Connect, null, false);
            case LinkState.PortBusy:
                return serial
                    ? new($"{i.Port ?? "The USB port"} is in use by another app", LinkTone.Danger,
                          "Close the serial monitor or Arduino IDE.", CardAction.Retry, null, true)
                    : new($"Port {i.Port} is in use by another app", LinkTone.Danger,
                          "Choose another port, here and on the controller.", CardAction.Retry, null, true);
            case LinkState.NoAnswer:
                return NoAnswer(i);
            case LinkState.Searching:
                return new("Looking for your controller", LinkTone.Neutral,
                           serial ? "Plug it in with a USB cable." : $"Listening on port {i.Port}.", CardAction.None, null, true);
            case LinkState.Incompatible:
                return Incompatible(banner);
        }
        if (banner.Kind == BannerKind.NoAnswer) return NoAnswer(i);
        if (banner.Kind == BannerKind.Incompatible) return Incompatible(banner);
        if (link.State == LinkState.Waiting)
            return new(LinkText.Activity(link.Activity), LinkTone.Neutral, null, CardAction.None, null, true);

        switch (banner.Kind)
        {
            case BannerKind.NotSetUp or BannerKind.NotSetUpHasMirror:
                return new("Not set up yet", LinkTone.Accent, "Choose how to start, below.", CardAction.None, null, true);
            case BannerKind.Unusable:
                return new("The controller's setup can't be used", LinkTone.Warning,
                           "Start again from Volco's copy or from scratch, below.", CardAction.None, null, true);
            case BannerKind.BrokenWithFirmware:
                return new("Your setup doesn't work with this firmware", LinkTone.Warning,
                           "Fix it in the drawing, then Save.", CardAction.FixIt, null, true);
            case BannerKind.DifferentSetup:
                return new("This controller has a different setup", LinkTone.Warning,
                           "It isn't the one Volco saved for it.", CardAction.Compare, null, true);
            case BannerKind.ReadFailed:
                return new("Couldn't read the controller's setup", LinkTone.Danger, null, CardAction.Retry, null, true);
        }
        if (banner.FirmwareUpdateAvailable)
            return new("Firmware update available", LinkTone.Warning, "Your setup stays on the controller.", CardAction.Update, null, true);

        var status = i.Transport switch
        {
            ConnectionType.Serial => string.IsNullOrEmpty(i.Port) ? "Connected over USB" : $"Connected over USB ({i.Port})",
            ConnectionType.Udp => "Connected over Wi-Fi",
            _ => "Connected to the simulator",
        };
        return new(status, LinkTone.Good, banner.DevelopmentFirmware ? "Development firmware" : "Firmware up to date",
                   i.OfferTest ? CardAction.TestNow : CardAction.None, null, true);
    }

    /// <summary>Check firmware only where Volco can check it (USB).</summary>
    private static CardState NoAnswer(CardInputs i)
        => new(i.Transport == ConnectionType.Serial ? $"No controller answers on {i.Port ?? "the USB port"}"
                                                    : $"No controller answers on port {i.Port}",
               LinkTone.Danger, "It may not have Volco's firmware yet.",
               i.Banner.Actions.HasFlag(BannerActions.CheckFirmware) ? CardAction.CheckFirmware : CardAction.None, null, true);

    /// <summary>Update only where Volco can update it (USB, with its firmware bundled).</summary>
    private static CardState Incompatible(ControllerBanner banner)
        => new("Firmware too old for this Volco", LinkTone.Warning, "Update it to use the controller again.",
               banner.Actions.HasFlag(BannerActions.Update) ? CardAction.Update : CardAction.None, null, true);

    /// <summary>The primary button's words.</summary>
    public static string Label(CardAction action) => action switch
    {
        CardAction.Connect => "Connect",
        CardAction.Retry => "Retry",
        CardAction.CheckFirmware => "Check firmware",
        CardAction.Update => "Update",
        CardAction.FixIt => "Fix it",
        CardAction.Compare => "Compare…",
        CardAction.TestNow => "Test your controller now",
        _ => "",
    };
}
