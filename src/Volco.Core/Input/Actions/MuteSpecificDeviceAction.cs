using Volco.Core.Audio;
using Volco.Core.Config;

namespace Volco.Core.Input.Actions;

public class MuteSpecificDeviceAction : IButtonAction
{
    private readonly IAudioEngine _audio;
    private readonly DeviceRef? _device;

    public MuteSpecificDeviceAction(IAudioEngine audio, DeviceRefConfig? device)
    {
        _audio = audio;
        _device = ActionFactory.Resolve(device);
    }

    public void Execute()
    {
        if (_device is not { } device) return;   // unconfigured -> no-op
        _audio.ToggleDeviceMute(device);
    }
}
