using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Devices;

namespace Volco.App.ViewModels;

/// <summary>How a device-picker entry reads: the display name, and the Windows name as secondary
/// text when it differs (spec §6.2).</summary>
public sealed record DeviceChoice(string Label, string Secondary)
{
    public bool HasSecondary => Secondary.Length > 0;

    /// <summary>Describes a picker entry (a Windows friendly name) using the device list and settings.</summary>
    public static DeviceChoice For(AppConfig config, IReadOnlyList<AudioDevice> devices, string friendlyName)
    {
        var device = devices.FirstOrDefault(d => string.Equals(d.FriendlyName, friendlyName, StringComparison.OrdinalIgnoreCase));
        var label = device is not null ? DeviceDisplay.For(config, device).Name : DeviceDisplay.AutoShortName(friendlyName);
        return new DeviceChoice(label, label == friendlyName ? "" : friendlyName);
    }
}

/// <summary>A view model whose device combo boxes show <see cref="DeviceChoice"/> labels.</summary>
public interface IDeviceLabeler
{
    DeviceChoice Describe(string friendlyName);
}
