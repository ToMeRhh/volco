using Volco.Audio;

namespace Volco.Input.Actions;

public class MuteCurrentOutputAction : IButtonAction
{
    private readonly IAudioManager _audio;
    public MuteCurrentOutputAction(IAudioManager audio) => _audio = audio;
    public void Execute() => _audio.ToggleMuteOutput();
}
