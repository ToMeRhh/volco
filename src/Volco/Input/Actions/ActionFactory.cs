using Volco.Audio;
using Volco.Config;

namespace Volco.Input.Actions;

public static class ActionFactory
{
    public static IButtonAction? Create(ButtonActionConfig config, IAudioManager audio, ConfigManager configManager)
    {
        return config.Action switch
        {
            ButtonActionType.MuteCurrentOutput => new MuteCurrentOutputAction(audio),
            ButtonActionType.MuteCurrentInput => new MuteCurrentInputAction(audio),
            ButtonActionType.MuteSpecificDevice => new MuteSpecificDeviceAction(audio, config.Device?.FriendlyName ?? ""),
            ButtonActionType.ToggleOutputDevice => new ToggleOutputDeviceAction(audio, configManager),
            ButtonActionType.SetOutputDevice => new SetOutputDeviceAction(audio, config.Device?.FriendlyName ?? ""),
            ButtonActionType.None => null,
            _ => null
        };
    }
}
