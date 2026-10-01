using Volco.Core.Audio;
using Volco.Core.Config;

namespace Volco.App.ViewModels;

/// <summary>
/// Bridges the settings combo boxes (which bind to friendly-name strings) and the v2
/// config (which stores endpoint ids). Resolving here means the id is captured at the
/// moment the user picks a device, rather than being looked up later when the name may
/// no longer match anything.
/// </summary>
internal static class DeviceRefLookup
{
    /// <summary>The device list the ViewModels were constructed with. Set once per VM tree.</summary>
    public static IReadOnlyList<AudioDevice> Devices { get; set; } = Array.Empty<AudioDevice>();

    /// <summary>Null when <paramref name="friendlyName"/> is blank, so "no selection" round-trips.</summary>
    public static DeviceRefConfig? DeviceRefFromName(string? friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName)) return null;

        var match = Devices.FirstOrDefault(d =>
            string.Equals(d.FriendlyName, friendlyName, StringComparison.OrdinalIgnoreCase));

        return match != null
            ? new DeviceRefConfig { Id = match.Id, FriendlyName = match.FriendlyName }
            : new DeviceRefConfig { Id = "", FriendlyName = friendlyName };
    }
}
