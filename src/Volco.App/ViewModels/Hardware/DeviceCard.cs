using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Runtime;

namespace Volco.App.ViewModels.Hardware;

/// <summary>The states that open the attention dialog when they begin (controller-page spec §5):
/// the controller can't be used until the user acts.</summary>
public enum AttentionKind { None, NoAnswer, Incompatible }

/// <summary>The status's one primary button (hardware-page spec §4.1; controller-page spec §5).</summary>
public enum CardAction { None, Connect, Retry, CheckFirmware, Update, FixIt, Compare, TestNow, Install, Reinstall, ChooseHowToStart }

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
/// <param name="OfferInstall">The last Check firmware found other firmware (or none it knows): the card
/// offers an install, which asks first, until the result goes.</param>
/// <param name="OfferFreshInstall">That install is a first one (the board never answered as Volco):
/// Install firmware; otherwise Reinstall firmware….</param>
public sealed record CardInputs(LinkStatus Link, ControllerBanner Banner, ConnectionType Transport, string? Port,
                                string? FirmwareStage, double? FirmwareProgress, string? SavePhase, string? Failure,
                                bool OfferTest, bool OfferInstall = false, bool OfferFreshInstall = false);

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

    /// <summary>The not-set-up state's detail line when Volco has a copy; the button (Choose how to
    /// start) says the rest.</summary>
    public const string HasMirrorDetail = "Volco has a saved copy for it.";
    public const string UnusableDetail = "Start again from Volco's copy or from scratch.";

    public static CardState From(CardInputs i)
    {
        var state = Row(i);
        // A running operation and the user's Disconnect keep their own detail line.
        bool keepsDetail = i.FirmwareStage is not null || i.SavePhase is not null || i.Banner.Kind == BannerKind.Saving
                           || i.Link.State == LinkState.Disconnected;
        if (i.Failure is { } failure && !keepsDetail) state = state with { Detail = failure };
        // Check firmware's "Reinstall it?" is answered by the card's button.
        if (i.OfferInstall && !keepsDetail)
            state = state with { Action = i.OfferFreshInstall ? CardAction.Install : CardAction.Reinstall };
        return state;
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
            case BannerKind.NotSetUp:
                return new("Not set up yet", LinkTone.Accent, null, CardAction.ChooseHowToStart, null, true);
            case BannerKind.NotSetUpHasMirror:
                return new("Not set up yet", LinkTone.Accent, HasMirrorDetail, CardAction.ChooseHowToStart, null, true);
            case BannerKind.Unusable:
                return new("The controller's setup can't be used", LinkTone.Warning,
                           UnusableDetail, CardAction.ChooseHowToStart, null, true);
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

    /// <summary>
    /// How much <paramref name="state"/> (<see cref="From"/> of <paramref name="inputs"/>) needs the
    /// user (controller-page spec §5): <see cref="StatusLevel.Running"/> while a firmware operation
    /// or a save runs; <see cref="StatusLevel.Fine"/> when connected and in sync (any firmware line
    /// but an update) and in the expected waits; <see cref="StatusLevel.NeedsYou"/> otherwise,
    /// a kept failure and an offered next step included.
    /// </summary>
    public static StatusLevel LevelOf(CardInputs inputs, CardState state)
    {
        if (inputs.FirmwareStage is not null || inputs.SavePhase is not null || inputs.Banner.Kind == BannerKind.Saving)
            return StatusLevel.Running;
        if (inputs.Failure is not null || state.Action != CardAction.None) return StatusLevel.NeedsYou;
        return inputs.Link.State switch
        {
            LinkState.Connected when state.Tone == LinkTone.Good => StatusLevel.Fine,
            LinkState.Waiting when state.Tone == LinkTone.Neutral => StatusLevel.Fine,
            _ => StatusLevel.NeedsYou,
        };
    }

    /// <summary>
    /// The sidebar status line's words for <paramref name="state"/> at <paramref name="level"/>
    /// (controller-page spec §5): "Connected" or "Connecting…" when all is fine; the running step
    /// while an operation runs; when the status needs the user, a word or two for why (the banner
    /// and the line's tooltip say the rest).
    /// </summary>
    public static string ShortStatus(CardInputs inputs, CardState state, StatusLevel level)
    {
        if (level == StatusLevel.Fine) return state.Tone == LinkTone.Good ? "Connected" : "Connecting…";
        if (level == StatusLevel.Running) return state.Status;
        switch (inputs.Link.State)
        {
            case LinkState.Disconnected: return "Disconnected";
            case LinkState.PortBusy: return "Port busy";
            case LinkState.NoAnswer or LinkState.Searching: return "Not connected";
            case LinkState.Incompatible: return "Firmware too old";
        }
        switch (inputs.Banner.Kind)
        {
            case BannerKind.NoAnswer: return "Not connected";
            case BannerKind.Incompatible: return "Firmware too old";
            case BannerKind.NotSetUp or BannerKind.NotSetUpHasMirror: return "Not set up";
            case BannerKind.DifferentSetup: return "Different setup";
        }
        if (inputs.Failure is null && state.Action == CardAction.Update) return "Update available";
        return "Needs attention";
    }

    /// <summary>
    /// Which attention state <paramref name="i"/> is in (controller-page spec §5): the rows of
    /// <see cref="From"/> that show "No controller answers" and "Firmware too old for this Volco",
    /// else <see cref="AttentionKind.None"/>. Null while an operation runs or the link waits as
    /// expected (a reconnect, a check's reset): those neither begin nor end a state, so a check that
    /// finds the board still silent doesn't open the dialog again.
    /// </summary>
    public static AttentionKind? AttentionOf(CardInputs i)
    {
        if (i.FirmwareStage is not null || i.SavePhase is not null || i.Banner.Kind == BannerKind.Saving) return null;
        switch (i.Link.State)
        {
            case LinkState.Disconnected or LinkState.PortBusy or LinkState.Searching: return AttentionKind.None;
            case LinkState.NoAnswer: return AttentionKind.NoAnswer;
            case LinkState.Incompatible: return AttentionKind.Incompatible;
        }
        if (i.Banner.Kind == BannerKind.NoAnswer) return AttentionKind.NoAnswer;
        if (i.Banner.Kind == BannerKind.Incompatible) return AttentionKind.Incompatible;
        return i.Link.State == LinkState.Waiting ? null : AttentionKind.None;
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
        CardAction.Install => "Install firmware",
        CardAction.Reinstall => "Reinstall firmware…",
        CardAction.ChooseHowToStart => "Choose how to start",
        _ => "",
    };
}
