using Volco.Core.Config;

namespace Volco.Core.Controller;

/// <summary>What <see cref="ControllerConfigService"/> needs from the controller session
/// (spec §8.2). <c>VolcoRuntime</c> implements it over <c>ControllerLink.SendRaw</c>.</summary>
public interface IConfigSessionLink
{
    /// <summary>Sends a whole burst as one transport send. False when no connection is attached.</summary>
    bool SendRaw(byte[] burst);

    /// <summary>The configured transport (picks the confirming timeout, and the firmware notice).</summary>
    ConnectionType ConnectionType { get; }
}
