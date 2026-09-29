using Volco.Core.Audio;

namespace Volco.Core.Input.Actions;

public class MuteCurrentInputAction : IButtonAction
{
    private readonly IAudioEngine _audio;
    public MuteCurrentInputAction(IAudioEngine audio) => _audio = audio;
    public void Execute()
        => _audio.ToggleDeviceMute(DeviceRef.DefaultInput());
}
