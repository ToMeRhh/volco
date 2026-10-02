using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Devices;
using Volco.Core.Input.Actions;

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

    /// <summary>How many link ticks a preset change is announced for (6 × 250 ms ≈ 1.5 s): a
    /// single LED is on in the even steps, so it blinks 3 times.</summary>
    public const int AnnounceSteps = 6;

    /// <summary>One entry per controller button; null where the kind is None or unknown. Empty
    /// until the controller has said hello. <paramref name="announceStep"/> is the link tick since
    /// the last preset change (0 = the change itself), or null when no change is being announced.</summary>
    public static IReadOnlyList<ButtonDisplay?> Compute(AppConfig config, string presetId, IAudioEngine audio, int? announceStep = null)
    {
        var info = config.Controller;
        if (info is null) return Array.Empty<ButtonDisplay?>();
        var result = new ButtonDisplay?[info.Buttons.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = For(config, i, info.Buttons[i], presetId, audio, announceStep);
        return result;
    }

    public static ButtonDisplay? For(AppConfig config, int button, IndicatorKind kind, string presetId, IAudioEngine audio,
                                     int? announceStep = null)
    {
        var binding = button >= 0 && button < config.Buttons.Count ? config.Buttons[button].BindingFor(presetId) : null;
        var action = binding is null ? null : Primary(binding);
        var announcing = announceStep is >= 0 and < AnnounceSteps && binding is not null;
        return kind switch
        {
            IndicatorKind.Led when announcing && BlinksOnPresetChange(binding!) => new LedDisplay(Bit(announceStep % 2 == 0)),
            IndicatorKind.Led or IndicatorKind.LedPair => new LedDisplay(LedValue(action, kind, config, presetId, audio)),
            IndicatorKind.Screen when announcing && FlashesPresetName(binding!) => new ScreenDisplay(LivePresetScreen(config, presetId)),
            IndicatorKind.Screen => new ScreenDisplay(Screen(action, config, presetId, audio)),
            _ => null,
        };
    }

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
    public static byte LedValueFor(ButtonBindingConfig binding, IndicatorKind kind, AppConfig config, string presetId, IAudioEngine audio)
        => LedValue(Primary(binding), kind, config, presetId, audio);

    /// <summary>The steady LED value. 0 = off; mute: 1 while muted; switch: 1 while active.
    /// An LED pair: toggle 1 = A, 2 = B; presets 1 + the live preset's position. A single LED:
    /// toggle lit on B, dark on A; "Go to preset" lit while that preset is live; "Next preset" dark
    /// (it blinks on a change instead).</summary>
    public static byte LedValue(ButtonActionConfig? action, IndicatorKind kind, AppConfig config, string presetId, IAudioEngine audio)
    {
        var single = kind == IndicatorKind.Led;
        switch (action?.Action)
        {
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

    /// <summary>Layout 1 content for an action (spec §2.2's table).</summary>
    public static ScreenContent Screen(ButtonActionConfig? action, AppConfig config, string presetId, IAudioEngine audio)
    {
        switch (action?.Action)
        {
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
