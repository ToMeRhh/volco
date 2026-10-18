using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Media;
using Volco.Core.Runtime;

namespace Volco.Core.Input.Actions;

public static class ActionFactory
{
    /// <summary>Builds the action for <paramref name="config"/>, or null for None. Mode actions
    /// need <paramref name="modes"/>; without one they are null too.</summary>
    public static IButtonAction? Create(ButtonActionConfig config, IAudioEngine audio, IModeController? modes)
        => Create(config, new ActionServices(audio, modes));

    /// <summary>Builds the action for <paramref name="config"/>, or null for None and for an
    /// action whose service <paramref name="services"/> lacks. <paramref name="origin"/> is the
    /// press, for a script's environment.</summary>
    public static IButtonAction? Create(ButtonActionConfig config, ActionServices services, ActionOrigin? origin = null)
        => config.Action switch
        {
            ButtonActionType.MuteCurrentOutput  => new MuteCurrentOutputAction(services.Audio),
            ButtonActionType.MuteCurrentInput   => new MuteCurrentInputAction(services.Audio),
            ButtonActionType.MuteSpecificDevice => new MuteSpecificDeviceAction(services.Audio, config.Device),
            ButtonActionType.ToggleOutputDevice => new ToggleOutputDeviceAction(services.Audio, config.Device, config.DeviceB),
            ButtonActionType.SetOutputDevice    => new SetOutputDeviceAction(services.Audio, config.Device),
            ButtonActionType.NextMode when services.Modes is { } p => new NextModeAction(p),
            ButtonActionType.GoToMode when services.Modes is { } p => new GoToModeAction(p, config.ModeId),
            ButtonActionType.MediaPlayPause when services.Media is { } m => new MediaAction(m, config.MediaApp, MediaCommand.PlayPause, origin?.ReportError),
            ButtonActionType.MediaNext when services.Media is { } m      => new MediaAction(m, config.MediaApp, MediaCommand.Next, origin?.ReportError),
            ButtonActionType.MediaPrevious when services.Media is { } m  => new MediaAction(m, config.MediaApp, MediaCommand.Previous, origin?.ReportError),
            ButtonActionType.MediaStop when services.Media is { } m      => new MediaAction(m, config.MediaApp, MediaCommand.Stop, origin?.ReportError),
            ButtonActionType.OpenProgram when services.Programs is { } o => new OpenProgramAction(o, config.Command, origin?.ReportError),
            ButtonActionType.RunScript when services.Scripts is { } s    => new RunScriptAction(s, config, origin),
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
