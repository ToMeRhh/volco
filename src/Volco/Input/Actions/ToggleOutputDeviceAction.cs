using Volco.Audio;
using Volco.Config;

namespace Volco.Input.Actions;

public class ToggleOutputDeviceAction : IButtonAction
{
    private readonly IAudioManager _audio;
    private readonly ConfigManager _configManager;

    public ToggleOutputDeviceAction(IAudioManager audio, ConfigManager configManager)
    {
        _audio = audio;
        _configManager = configManager;
    }

    public void Execute()
    {
        var devices = _configManager.Config.OutputDevices;
        var currentDevice = _audio.GetDefaultOutputDeviceName();
        var isOnDevice1 = currentDevice != null &&
            currentDevice.Contains(devices.Device1, StringComparison.OrdinalIgnoreCase);
        var target = isOnDevice1 ? devices.Device2 : devices.Device1;
        _audio.SetDefaultOutputDevice(target);
    }
}
