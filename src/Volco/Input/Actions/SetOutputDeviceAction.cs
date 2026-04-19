using Volco.Audio;

namespace Volco.Input.Actions;

public class SetOutputDeviceAction : IButtonAction
{
    private readonly IAudioManager _audio;
    private readonly string _deviceId;

    public SetOutputDeviceAction(IAudioManager audio, string deviceId)
    {
        _audio = audio;
        _deviceId = deviceId;
    }

    public void Execute() => _audio.SetDefaultOutputDevice(_deviceId);
}
