using Volco.Audio;

namespace Volco.Input.Actions;

public class MuteCurrentInputAction : IButtonAction
{
    private readonly IAudioManager _audio;
    public MuteCurrentInputAction(IAudioManager audio) => _audio = audio;
    public void Execute() => _audio.ToggleMuteInput();
}
