namespace Volco.Core.Runtime;

/// <summary>Where the controller session stands (spec §5.5), separate from the transport's
/// <see cref="Communication.TransportStatus"/>.</summary>
public enum ControllerState
{
    /// <summary>The transport is not connected.</summary>
    NoController,
    /// <summary>Connected; HelloRequest sent, no Hello yet. Nothing else is sent.</summary>
    AwaitingHello,
    /// <summary>A Hello with this app's protocol version arrived.</summary>
    Ready,
    /// <summary>A Hello with another protocol version arrived: the firmware needs reflashing.</summary>
    VersionMismatch,
}
