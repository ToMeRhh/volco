using Volco.Core.Audio;
using Volco.Core.Config;

namespace Volco.Core.Input.Actions;

public static class ActionFactory
{
    public static IButtonAction? Create(ButtonActionConfig config, IAudioEngine audio, ConfigManager configManager)
        => config.Action switch
        {
            ButtonActionType.MuteCurrentOutput  => new MuteCurrentOutputAction(audio),
            ButtonActionType.MuteCurrentInput   => new MuteCurrentInputAction(audio),
            ButtonActionType.MuteSpecificDevice => new MuteSpecificDeviceAction(audio, config.Device),
            ButtonActionType.ToggleOutputDevice => new ToggleOutputDeviceAction(audio, configManager),
            ButtonActionType.SetOutputDevice    => new SetOutputDeviceAction(audio, config.Device),
            _                                   => null
        };

    /// <summary>
    /// Resolves a configured device reference, or null when the config carries neither an
    /// id nor a name — in which case the caller must do nothing rather than guess.
    /// </summary>
    internal static DeviceRef? Resolve(DeviceRefConfig? cfg)
    {
        if (cfg == null) return null;
        if (!string.IsNullOrEmpty(cfg.Id)) return DeviceRef.ById(cfg.Id);
        if (!string.IsNullOrEmpty(cfg.FriendlyName)) return DeviceRef.ByFriendlyName(cfg.FriendlyName);
        return null;
    }
}
