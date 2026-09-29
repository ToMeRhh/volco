using Volco.Core.Audio;

namespace Volco.Core.Input.Actions;

public class MuteCurrentOutputAction : IButtonAction
{
    private readonly IAudioEngine _audio;
    public MuteCurrentOutputAction(IAudioEngine audio) => _audio = audio;
    public void Execute()
        => _audio.ToggleDeviceMute(DeviceRef.DefaultOutput());
}
