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
        var devices = _configManager.Config.OutputDevices;
        var current = _audio.GetDefaultDevice(AudioFlow.Render);

        // On device 1 -> go to 2. Anywhere else (including a third device) -> go to 1.
        var onDevice1 = current != null && DeviceMatches(current, devices.Device1);
        var target = onDevice1 ? devices.Device2 : devices.Device1;

        if (string.IsNullOrEmpty(target?.Id)) return;
        _audio.SetDefaultDevice(target.Id, AudioFlow.Render);
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
