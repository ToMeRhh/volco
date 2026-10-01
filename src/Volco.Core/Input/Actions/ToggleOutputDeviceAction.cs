using Volco.Core.Audio;
using Volco.Core.Config;

namespace Volco.Core.Input.Actions;

/// <summary>Switches the default output between the action's own two devices (A and B).</summary>
public class ToggleOutputDeviceAction : IButtonAction
{
    private readonly IAudioEngine _audio;
    private readonly DeviceRefConfig? _deviceA;
    private readonly DeviceRefConfig? _deviceB;

    public ToggleOutputDeviceAction(IAudioEngine audio, DeviceRefConfig? deviceA, DeviceRefConfig? deviceB)
    {
        _audio = audio;
        _deviceA = deviceA;
        _deviceB = deviceB;
    }

    public void Execute()
    {
        var id = ResolveTargetId(_audio.GetDefaultDevice(AudioFlow.Render), _deviceA, _deviceB);
        if (id is null) return;
        _audio.SetDefaultDevice(id, AudioFlow.Render);
    }

    /// <summary>The endpoint id a toggle press switches to, or null when that slot has no id.
    /// Shared with <see cref="Runtime.VolcoRuntime"/>, which needs the target before Execute()
    /// for the OSD.</summary>
    internal static string? ResolveTargetId(AudioDevice? current, DeviceRefConfig? deviceA, DeviceRefConfig? deviceB)
    {
        // On A -> go to B. Anywhere else (including a third device) -> go to A.
        var onA = current != null && DeviceMatches(current, deviceA);
        var target = onA ? deviceB : deviceA;
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
