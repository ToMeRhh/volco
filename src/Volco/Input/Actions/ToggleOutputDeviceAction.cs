using Volco.Audio;
using Volco.Config;

namespace Volco.Input.Actions;

public class ToggleOutputDeviceAction : IButtonAction
{
    private readonly IAudioManager _audio;
    private readonly ConfigManager _configManager;
    private bool _onDevice1 = true;

    public ToggleOutputDeviceAction(IAudioManager audio, ConfigManager configManager)
    {
        _audio = audio;
        _configManager = configManager;
    }

    public void Execute()
    {
        var devices = _configManager.Config.OutputDevices;
        var target = _onDevice1 ? devices.Device2 : devices.Device1;
        _audio.SetDefaultOutputDevice(target);
        _onDevice1 = !_onDevice1;
    }
}
