using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Runtime;

namespace Volco.Core.Input.Actions;

public static class ActionFactory
{
    /// <summary>Builds the action for <paramref name="config"/>, or null for None. Preset actions
    /// need <paramref name="presets"/>; without one they are null too.</summary>
    public static IButtonAction? Create(ButtonActionConfig config, IAudioEngine audio, IPresetController? presets)
        => config.Action switch
        {
            ButtonActionType.MuteCurrentOutput  => new MuteCurrentOutputAction(audio),
            ButtonActionType.MuteCurrentInput   => new MuteCurrentInputAction(audio),
            ButtonActionType.MuteSpecificDevice => new MuteSpecificDeviceAction(audio, config.Device),
            ButtonActionType.ToggleOutputDevice => new ToggleOutputDeviceAction(audio, config.Device, config.DeviceB),
            ButtonActionType.SetOutputDevice    => new SetOutputDeviceAction(audio, config.Device),
            ButtonActionType.NextPreset when presets != null => new NextPresetAction(presets),
            ButtonActionType.GoToPreset when presets != null => new GoToPresetAction(presets, config.PresetId),
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
