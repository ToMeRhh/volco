using Volco.Core.Communication;

namespace Volco.Core.Runtime;

/// <summary>
/// What the user is told about the controller (spec: 2026-10-06 link state). "Connected" means
/// Volco and the controller talk the same protocol: a Hello with this app's version arrived. A
/// port that is merely open, or a packet that merely arrived, is never "connected". Every UI
/// surface (tray, sidebar card, Connection and Controller pages) reads only this.
/// </summary>
public enum LinkState
{
    /// <summary>The user clicked Disconnect. The only state that says "Disconnected".</summary>
    Disconnected,
    /// <summary>Nothing open: the USB port isn't there, or nothing has sent a UDP/WebSocket packet.</summary>
    Searching,
    /// <summary>The port can't be used (another app holds it, or no USB port is chosen).</summary>
    PortBusy,
    /// <summary>An expected wait: connecting, a firmware operation, or a restart after a save.
    /// See <see cref="LinkActivity"/>. Never shown as a failure.</summary>
    Waiting,
    /// <summary>The USB port is open but nothing answers as a Volco controller.</summary>
    NoAnswer,
    /// <summary>The controller answered with another protocol version: its firmware needs updating.</summary>
    Incompatible,
    /// <summary>A Hello with this app's protocol version: Volco can use the controller.</summary>
    Connected,
}

/// <summary>What a <see cref="LinkState.Waiting"/> link is waiting for.</summary>
public enum LinkActivity
{
    None,
    /// <summary>A transport just started, or is open and the Hello hasn't come yet.</summary>
    Connecting,
    CheckingFirmware,
    InstallingFirmware,
    UpdatingFirmware,
    /// <summary>A config save: the controller restarts into its new setup.</summary>
    Restarting,
}

public sealed record LinkStatus(LinkState State, LinkActivity Activity = LinkActivity.None)
{
    public static LinkStatus Disconnected { get; } = new(LinkState.Disconnected);

    public bool IsConnected => State == LinkState.Connected;

    /// <summary>Not connected and not an expected wait: the tray turns red.</summary>
    public bool IsDown => State is not (LinkState.Connected or LinkState.Waiting);
}

/// <param name="Interruption">The activity of the newest expected interruption still held, or
/// <see cref="LinkActivity.None"/>.</param>
/// <param name="Saving">A config save is running (the controller restarts at its end).</param>
/// <param name="Silent">The USB port has been open for a while with no Hello.</param>
public sealed record LinkInputs(bool UserDisconnected, TransportStatus Transport, ControllerState Controller,
                                bool Silent, LinkActivity Interruption, bool Saving);

public static class LinkStateLogic
{
    /// <summary>The link state as a pure function of the runtime's inputs. A Hello wins over an
    /// interruption (a controller that answered is connected, or incompatible, whatever Volco
    /// expected); an interruption wins over everything the transport says.</summary>
    public static LinkStatus Compute(LinkInputs i)
    {
        if (i.UserDisconnected) return LinkStatus.Disconnected;
        if (i.Controller == ControllerState.Ready) return new(LinkState.Connected);
        if (i.Controller == ControllerState.VersionMismatch) return new(LinkState.Incompatible);
        if (i.Interruption != LinkActivity.None) return new(LinkState.Waiting, i.Interruption);
        if (i.Saving) return new(LinkState.Waiting, LinkActivity.Restarting);
        return i.Transport switch
        {
            TransportStatus.Open => i.Silent ? new(LinkState.NoAnswer) : new(LinkState.Waiting, LinkActivity.Connecting),
            TransportStatus.Failed => new(LinkState.PortBusy),
            _ => new(LinkState.Searching),
        };
    }
}
