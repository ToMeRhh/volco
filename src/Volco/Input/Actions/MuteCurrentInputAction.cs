using Volco.Audio;

namespace Volco.Input.Actions;

public class MuteCurrentInputAction : IButtonAction
{
    private readonly IAudioEngine _audio;
    public MuteCurrentInputAction(IAudioEngine audio) => _audio = audio;
    public void Execute()
        => _audio.ToggleDeviceMute(DeviceRef.Default(AudioFlow.Capture, AudioRole.Communications));
}
