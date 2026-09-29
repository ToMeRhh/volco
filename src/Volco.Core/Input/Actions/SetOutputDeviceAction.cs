using Volco.Core.Audio;
using Volco.Core.Config;

namespace Volco.Core.Input.Actions;

public class SetOutputDeviceAction : IButtonAction
{
    private readonly IAudioEngine _audio;
    private readonly DeviceRefConfig? _device;

    public SetOutputDeviceAction(IAudioEngine audio, DeviceRefConfig? device)
    {
        _audio = audio;
        _device = device;
    }

    public void Execute()
    {
        var id = _device?.Id;
        if (string.IsNullOrEmpty(id)) return;    // unconfigured -> no-op
        _audio.SetDefaultDevice(id, AudioFlow.Render);
    }
}
