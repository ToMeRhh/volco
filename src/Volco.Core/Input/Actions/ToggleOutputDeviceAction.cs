using Volco.Core.Audio;
using Volco.Core.Config;

namespace Volco.Core.Input.Actions;

public class ToggleOutputDeviceAction : IButtonAction
{
    private readonly IAudioEngine _audio;
    private readonly ConfigManager _configManager;

    public ToggleOutputDeviceAction(IAudioEngine audio, ConfigManager configManager)
    {
        _audio = audio;
        _configManager = configManager;
    }

    public void Execute()
    {
        var id = ResolveTargetId(_audio.GetDefaultDevice(AudioFlow.Render), _configManager.Config.OutputDevices);
        if (id is null) return;
        _audio.SetDefaultDevice(id, AudioFlow.Render);
    }

    /// <summary>The endpoint id a toggle press switches to, or null when that slot has no id.
    /// Shared with <see cref="Runtime.VolcoRuntime"/>, which needs the target before Execute()
    /// for the OSD.</summary>
    internal static string? ResolveTargetId(AudioDevice? current, OutputDevicesConfig devices)
    {
        // On device 1 -> go to 2. Anywhere else (including a third device) -> go to 1.
        var onDevice1 = current != null && DeviceMatches(current, devices.Device1);
        var target = onDevice1 ? devices.Device2 : devices.Device1;
        return target?.Id is { Length: > 0 } id ? id : null;
    }

    internal static bool DeviceMatches(AudioDevice device, DeviceRefConfig? cfg)
    {
        if (cfg == null) return false;
        if (!string.IsNullOrEmpty(cfg.Id))
            return string.Equals(device.Id, cfg.Id, StringComparison.OrdinalIgnoreCase);
        return !string.IsNullOrEmpty(cfg.FriendlyName)
            && string.Equals(device.FriendlyName, cfg.FriendlyName, StringComparison.OrdinalIgnoreCase);
    }
}
