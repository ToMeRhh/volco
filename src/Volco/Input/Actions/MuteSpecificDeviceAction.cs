using Volco.Audio;

namespace Volco.Input.Actions;

public class MuteSpecificDeviceAction : IButtonAction
{
    private readonly IAudioManager _audio;
    private readonly string _deviceId;

    public MuteSpecificDeviceAction(IAudioManager audio, string deviceId)
    {
        _audio = audio;
        _deviceId = deviceId;
    }

    public void Execute() => _audio.ToggleMuteOutput(_deviceId);
}
