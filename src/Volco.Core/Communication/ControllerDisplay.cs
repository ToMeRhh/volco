using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Devices;
using Volco.Core.Input.Actions;
using Volco.Core.Media;
using Volco.Core.Programs;
using Volco.Core.Runtime;

namespace Volco.Core.Communication;

/// <summary>What one controller button shows: an LED value, or a screen.</summary>
public abstract record ButtonDisplay;
public sealed record LedDisplay(byte Value) : ButtonDisplay;
public sealed record ScreenDisplay(ScreenContent Content) : ButtonDisplay;

/// <summary>
/// Computes what every controller button shows (spec §2.2, §5.3). Pure: only read-only engine
/// queries. One entry per button the controller reported in Hello (not per config card): a
/// button with no card is "Not set up", and cards beyond the controller's count are ignored.
/// A button shows its first action that is not "Do nothing", in the order press → double → hold.
/// <para>
/// For <see cref="AnnounceSteps"/> link ticks after a preset change, a button with a preset action
/// announces it: a screen that shows another action flashes the live preset instead
/// (<see cref="FlashesPresetName"/>), and a single LED blinks (<see cref="BlinksOnPresetChange"/>).
/// </para>
/// <para>
/// A screen shows another of the button's actions for a moment after it is pressed (the
/// runtime's <c>pressed</c> map, <see cref="ShowsWhenPressed"/>), and a script action in its
/// failure flash shows on the screen and the light whichever slot it is on (custom-actions spec §11).
/// </para>
/// </summary>
public static class ControllerDisplay
{
    public const int MaxDots = 12;
    public const string Muted = "MUTED";
    public const string Live = "LIVE";
    public const string Active = "ACTIVE";
    public const string ChooseDevice = "Choose a device";
    public const string NoDevice = "No device";
    public const string ChoosePreset = "Choose a preset";
    public const string Playing = "PLAYING";
    public const string Paused = "PAUSED";
    public const string Stopped = "STOPPED";
    public const string Running = "RUNNING";
    public const string Failed = "FAILED";
    public const string ChooseFile = "Choose a file";

    /// <summary>A media button's name when it follows the current app and there is none.</summary>
    public const string MediaName = "Media";

    /// <summary>How many link ticks a preset change is announced for (6 × 250 ms ≈ 1.5 s): a
    /// single LED is on in the even steps, so it blinks 3 times.</summary>
    public const int AnnounceSteps = 6;

    /// <summary>One entry per controller button; null where the kind is None or unknown. Empty
    /// until the controller has said hello. <paramref name="announceStep"/> is the link tick since
    /// the last preset change (0 = the change itself), or null when no change is being announced.
    /// <paramref name="status"/> is the live state of media, program and script actions (null: idle).
    /// <paramref name="pressed"/> maps a button to the action a press just ran, while the screen
    /// shows it instead of the button's first action.</summary>
    public static IReadOnlyList<ButtonDisplay?> Compute(AppConfig config, string presetId, IAudioEngine audio, int? announceStep = null,
                                                        IActionStatus? status = null,
                                                        IReadOnlyDictionary<int, ButtonActionConfig>? pressed = null)
    {
        var info = config.Controller;
        if (info is null) return Array.Empty<ButtonDisplay?>();
        var result = new ButtonDisplay?[info.Buttons.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = For(config, i, info.Buttons[i], presetId, audio, announceStep, status,
                            pressed is not null && pressed.TryGetValue(i, out var p) ? p : null);
        return result;
    }

    public static ButtonDisplay? For(AppConfig config, int button, IndicatorKind kind, string presetId, IAudioEngine audio,
                                     int? announceStep = null, IActionStatus? status = null, ButtonActionConfig? pressed = null)
    {
        var binding = button >= 0 && button < config.Buttons.Count ? config.Buttons[button].BindingFor(presetId) : null;
        var primary = binding is null ? null : Primary(binding);
        var failing = binding is null ? null : FailingScript(binding, status);
        var light = failing ?? primary;
        var screen = failing ?? (binding is not null && pressed is not null && ShowsWhenPressed(binding, pressed) ? pressed : primary);
        var announcing = announceStep is >= 0 and < AnnounceSteps && binding is not null;
        return kind switch
        {
            IndicatorKind.Led when announcing && BlinksOnPresetChange(binding!) => new LedDisplay(Bit(announceStep % 2 == 0)),
            IndicatorKind.Led or IndicatorKind.LedPair => new LedDisplay(LedValue(light, kind, config, presetId, audio, status)),
            IndicatorKind.Screen when announcing && FlashesPresetName(binding!) => new ScreenDisplay(LivePresetScreen(config, presetId)),
            IndicatorKind.Screen => new ScreenDisplay(Screen(screen, config, presetId, audio, status)),
            _ => null,
        };
    }

    /// <summary>Whether a press of <paramref name="pressed"/> makes the screen show it for a
    /// moment: one of the binding's actions (the same object), not its first one, and neither
    /// "Do nothing" nor a preset action (a preset change is announced on its own).</summary>
    public static bool ShowsWhenPressed(ButtonBindingConfig binding, ButtonActionConfig pressed)
        => pressed.Action != ButtonActionType.None && !IsPresetAction(pressed)
           && !ReferenceEquals(pressed, Primary(binding))
           && binding.All().Any(a => ReferenceEquals(a, pressed));

    /// <summary>A Run a script action of the binding that is in its failure flash, or null.</summary>
    private static ButtonActionConfig? FailingScript(ButtonBindingConfig binding, IActionStatus? status)
        => status is null ? null
           : binding.All().FirstOrDefault(a => a.Action == ButtonActionType.RunScript && status.Script(a).FlashStep is not null);

    /// <summary>A single LED blinks after a preset change when the button has a preset action,
    /// unless its light follows "Go to preset" (that light already says whether its preset is live).</summary>
    public static bool BlinksOnPresetChange(ButtonBindingConfig binding)
        => HasPresetAction(binding) && Primary(binding)?.Action != ButtonActionType.GoToPreset;

    /// <summary>A screen flashes the live preset after a preset change when the button has a preset
    /// action but its screen shows another action.</summary>
    public static bool FlashesPresetName(ButtonBindingConfig binding)
        => HasPresetAction(binding) && !IsPresetAction(Primary(binding));

    private static bool HasPresetAction(ButtonBindingConfig binding) => binding.All().Any(IsPresetAction);

    private static bool IsPresetAction(ButtonActionConfig? a)
        => a?.Action is ButtonActionType.NextPreset or ButtonActionType.GoToPreset;

    private static ButtonActionConfig? Primary(ButtonBindingConfig binding)
        => binding.All().FirstOrDefault(a => a.Action != ButtonActionType.None);

    /// <summary>The LED value for a whole binding (used by the settings UI's light preview).</summary>
    public static byte LedValueFor(ButtonBindingConfig binding, IndicatorKind kind, AppConfig config, string presetId, IAudioEngine audio,
                                   IActionStatus? status = null)
        => LedValue(Primary(binding), kind, config, presetId, audio, status);

    /// <summary>The steady LED value. 0 = off; mute: 1 while muted; switch: 1 while active.
    /// An LED pair: toggle 1 = A, 2 = B; presets 1 + the live preset's position. A single LED:
    /// toggle lit on B, dark on A; "Go to preset" lit while that preset is live; "Next preset" dark
    /// (it blinks on a change instead). Media: on while the target plays (Play/Pause, Stop). Program:
    /// on while it runs. Script: on while it runs, blinking during the failure flash.</summary>
    public static byte LedValue(ButtonActionConfig? action, IndicatorKind kind, AppConfig config, string presetId, IAudioEngine audio,
                                IActionStatus? status = null)
    {
        var single = kind == IndicatorKind.Led;
        status ??= ActionStatus.None;
        switch (action?.Action)
        {
            case ButtonActionType.MediaPlayPause:
            case ButtonActionType.MediaStop:
            {
                var media = status.Media(action.MediaApp);
                return Bit(media.Present && media.Playback == MediaPlayback.Playing);
            }
            case ButtonActionType.OpenProgram:
                return Bit(status.Program(action.Command) == ProgramState.Running);
            case ButtonActionType.RunScript:
            {
                var script = status.Script(action);
                return script.FlashStep is { } step ? Bit(step % 2 == 0) : Bit(script.Running);
            }
            case ButtonActionType.MuteCurrentOutput:
                return Bit(audio.GetDefaultDevice(AudioFlow.Render) is not null && audio.IsMuted(DeviceRef.DefaultOutput()));
            case ButtonActionType.MuteCurrentInput:
                return Bit(audio.GetDefaultDevice(AudioFlow.Capture) is not null && audio.IsMuted(DeviceRef.DefaultInput()));
            case ButtonActionType.MuteSpecificDevice:
                return Bit(DeviceDisplay.FindLive(audio, action.Device) is not null
                           && ActionFactory.Resolve(action.Device) is { } r && audio.IsMuted(r));
            case ButtonActionType.SetOutputDevice:
                return Bit(IsCurrentOutput(audio, action.Device));
            case ButtonActionType.ToggleOutputDevice:
            {
                var (a, b) = ToggleSides(action);
                if (a is null) return 0;
                if (IsCurrentOutput(audio, a)) return single ? (byte)0 : (byte)1;
                return IsCurrentOutput(audio, b) ? (single ? (byte)1 : (byte)2) : (byte)0;
            }
            case ButtonActionType.NextPreset when single:
                return 0;
            case ButtonActionType.GoToPreset when single:
                return Bit(action.PresetId == presetId);
            case ButtonActionType.NextPreset:
            case ButtonActionType.GoToPreset:
            {
                var index = config.Presets.FindIndex(p => p.Id == presetId);
                return index < 0 ? (byte)0 : (byte)Math.Min(255, index + 1);
            }
            default:
                return 0;
        }
    }

    /// <summary>Layout 1 content for an action (spec §2.2's table; custom-actions spec §3.3–§5.4):
    /// the top line says the state, the main line names the device, app, program or script.</summary>
    public static ScreenContent Screen(ButtonActionConfig? action, AppConfig config, string presetId, IAudioEngine audio,
                                       IActionStatus? status = null)
    {
        status ??= ActionStatus.None;
        switch (action?.Action)
        {
            case ButtonActionType.MediaPlayPause:
            case ButtonActionType.MediaStop:
            {
                var media = status.Media(action.MediaApp);
                var name = media.Name.Length > 0 ? media.Name : MediaName;
                if (!media.Present) return ScreenContent.Text("", ScreenIcon.Play, false, name);
                return media.Playback switch
                {
                    MediaPlayback.Playing => ScreenContent.Text(Playing, ScreenIcon.Play, false, name),
                    MediaPlayback.Paused => ScreenContent.Text(Paused, ScreenIcon.Pause, false, name),
                    _ => ScreenContent.Text(Stopped, ScreenIcon.Pause, false, name),
                };
            }
            case ButtonActionType.MediaNext:
            case ButtonActionType.MediaPrevious:
            {
                var media = status.Media(action.MediaApp);
                var name = media.Name.Length > 0 ? media.Name : MediaName;
                var icon = action.Action == ButtonActionType.MediaNext ? ScreenIcon.Next : ScreenIcon.Previous;
                return ScreenContent.Text("", icon, false, name);
            }
            case ButtonActionType.OpenProgram:
            {
                var name = CommandPath.DisplayName(action.Command);
                if (name.Length == 0) return ScreenContent.Text("", ScreenIcon.App, false, ChooseFile);
                return ScreenContent.Text(status.Program(action.Command) == ProgramState.Running ? Running : "", ScreenIcon.App, false, name);
            }
            case ButtonActionType.RunScript:
            {
                var name = CommandPath.DisplayName(action.Command);
                if (name.Length == 0) return ScreenContent.Text("", ScreenIcon.Script, false, ChooseFile);
                var script = status.Script(action);
                var top = script.FlashStep is not null ? Failed : script.Running ? Running : "";
                return ScreenContent.Text(top, ScreenIcon.Script, false, name);
            }
            case ButtonActionType.MuteCurrentOutput:
                return MuteScreen(config, audio, audio.GetDefaultDevice(AudioFlow.Render), DeviceRef.DefaultOutput());
            case ButtonActionType.MuteCurrentInput:
                return MuteScreen(config, audio, audio.GetDefaultDevice(AudioFlow.Capture), DeviceRef.DefaultInput());
            case ButtonActionType.MuteSpecificDevice:
            {
                if (DeviceDisplay.For(config, audio, action.Device) is not { } look) return AskForDevice();
                var live = DeviceDisplay.FindLive(audio, action.Device);
                if (live is null) return ScreenContent.Text("", look.Icon, false, look.Name);
                var muted = audio.IsMuted(DeviceRef.ById(live.Id, live.Flow));
                return ScreenContent.Text(muted ? Muted : Live, look.Icon, muted, look.Name);
            }
            case ButtonActionType.SetOutputDevice:
            {
                if (DeviceDisplay.For(config, audio, action.Device) is not { } look) return AskForDevice();
                return ScreenContent.Text(IsCurrentOutput(audio, action.Device) ? Active : "", look.Icon, false, look.Name);
            }
            case ButtonActionType.ToggleOutputDevice:
            {
                var (a, b) = ToggleSides(action);
                if (DeviceDisplay.For(config, audio, a) is not { } lookA) return AskForDevice();
                var lookB = DeviceDisplay.For(config, audio, b) ?? lookA;
                var onB = !IsCurrentOutput(audio, a) && IsCurrentOutput(audio, b);
                var (active, activeRef, other) = onB ? (lookB, b, lookA) : (lookA, a, lookB);
                if (DeviceDisplay.FindLive(audio, activeRef) is null)
                    return ScreenContent.Text("", active.Icon, false, active.Name);
                return ScreenContent.Text($"NEXT: {other.Name}", active.Icon, false, active.Name);
            }
            case ButtonActionType.NextPreset:
                return LivePresetScreen(config, presetId);
            case ButtonActionType.GoToPreset:
                return PresetScreen(config, presetId, config.Presets.Find(p => p.Id == action.PresetId)?.Name ?? ChoosePreset);
            default:
                return ScreenContent.Blank;
        }
    }

    private static ScreenContent MuteScreen(AppConfig config, IAudioEngine audio, AudioDevice? device, DeviceRef reference)
    {
        if (device is null) return ScreenContent.Text("", ScreenIcon.Generic, false, NoDevice);
        var look = DeviceDisplay.For(config, device);
        var muted = audio.IsMuted(reference);
        return ScreenContent.Text(muted ? Muted : Live, look.Icon, muted, look.Name);
    }

    private static ScreenContent LivePresetScreen(AppConfig config, string presetId)
        => PresetScreen(config, presetId, config.Presets.Find(p => p.Id == presetId)?.Name ?? "");

    private static ScreenContent PresetScreen(AppConfig config, string presetId, string main)
    {
        var count = config.Presets.Count;
        var index = Math.Max(0, config.Presets.FindIndex(p => p.Id == presetId));
        return count <= MaxDots
            ? ScreenContent.Dots((byte)index, (byte)count, ScreenIcon.Presets, main)
            : ScreenContent.Text($"{index + 1}/{count}", ScreenIcon.Presets, false, main);
    }

    private static ScreenContent AskForDevice() => ScreenContent.Text("", ScreenIcon.Generic, false, ChooseDevice);

    /// <summary>A toggle with one side set uses it as both A and B.</summary>
    private static (DeviceRefConfig? A, DeviceRefConfig? B) ToggleSides(ButtonActionConfig action)
    {
        var a = IsSet(action.Device) ? action.Device : IsSet(action.DeviceB) ? action.DeviceB : null;
        var b = IsSet(action.DeviceB) ? action.DeviceB : a;
        return (a, b);
    }

    private static bool IsSet(DeviceRefConfig? d)
        => d is not null && (!string.IsNullOrEmpty(d.Id) || !string.IsNullOrEmpty(d.FriendlyName));

    private static bool IsCurrentOutput(IAudioEngine audio, DeviceRefConfig? device)
        => audio.GetDefaultDevice(AudioFlow.Render) is { } current && ToggleOutputDeviceAction.DeviceMatches(current, device);

    private static byte Bit(bool on) => on ? (byte)1 : (byte)0;
}
