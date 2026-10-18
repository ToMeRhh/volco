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
/// For <see cref="AnnounceSteps"/> link ticks after a mode change, a button with a mode action
/// announces it: a screen that shows another action flashes the live mode instead
/// (<see cref="FlashesModeName"/>), and a single LED blinks (<see cref="BlinksOnModeChange"/>).
/// </para>
/// <para>
/// For <see cref="FeedbackSteps"/> × <see cref="FeedbackStep"/> after a press (a
/// <see cref="PressFeedback"/>, custom-actions spec §11): a press that did nothing shows the error
/// icon and why on a screen and flashes an LED fast; a press of another action than the one the
/// button shows (<see cref="ShowsWhenPressed"/>) shows that action on a screen and blinks an LED
/// twice; a Play/pause shows the state it expects until Windows reports it. A script action in its
/// failure flash shows on the screen and the light whichever slot it is on.
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
    public const string ChooseMode = "Choose a mode";
    public const string Playing = "PLAYING";
    public const string Paused = "PAUSED";
    public const string Stopped = "STOPPED";
    public const string Running = "RUNNING";
    public const string Failed = "FAILED";
    public const string ChooseFile = "Choose a file";

    // Why a press did nothing (the top line of the error screen).
    public const string NotRunning = "NOT RUNNING";
    public const string NoMedia = "NO MEDIA";
    public const string NotAvailable = "NOT AVAILABLE";
    public const string UsbOnly = "USB ONLY";
    public const string CantOpen = "CAN'T OPEN";
    public const string NoFile = "NO FILE";

    /// <summary>How long one step of a press's feedback lasts: an error flashes on and off every
    /// step, an OK blinks for 3 steps on, 3 off.</summary>
    public static TimeSpan FeedbackStep { get; } = TimeSpan.FromMilliseconds(125);

    /// <summary>How many steps a press's feedback lasts (12 × 125 ms = 1.5 s).</summary>
    public const int FeedbackSteps = 12;

    /// <summary>How many steps a Play/pause shows its expected state before the real one wins (1 s).</summary>
    public const int ExpectSteps = 8;

    /// <summary>A media button's name when it follows the current app and there is none.</summary>
    public const string MediaName = "Media";

    /// <summary>How many link ticks a mode change is announced for (6 × 250 ms ≈ 1.5 s): a
    /// single LED is on in the even steps, so it blinks 3 times.</summary>
    public const int AnnounceSteps = 6;

    /// <summary>One entry per controller button; null where the kind is None or unknown. Empty
    /// until the controller has said hello. <paramref name="announceStep"/> is the link tick since
    /// the last mode change (0 = the change itself), or null when no change is being announced.
    /// <paramref name="status"/> is the live state of media, program and script actions (null: idle).
    /// <paramref name="feedback"/> maps a button to its feedback for a press just made.</summary>
    public static IReadOnlyList<ButtonDisplay?> Compute(AppConfig config, string modeId, IAudioEngine audio, int? announceStep = null,
                                                        IActionStatus? status = null,
                                                        IReadOnlyDictionary<int, PressFeedback>? feedback = null)
    {
        var info = config.Controller;
        if (info is null) return Array.Empty<ButtonDisplay?>();
        var result = new ButtonDisplay?[info.Buttons.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = For(config, i, info.Buttons[i], modeId, audio, announceStep, status,
                            feedback is not null && feedback.TryGetValue(i, out var f) ? f : null);
        return result;
    }

    public static ButtonDisplay? For(AppConfig config, int button, IndicatorKind kind, string modeId, IAudioEngine audio,
                                     int? announceStep = null, IActionStatus? status = null, PressFeedback? feedback = null)
    {
        var binding = button >= 0 && button < config.Buttons.Count ? config.Buttons[button].BindingFor(modeId) : null;
        var primary = binding is null ? null : Primary(binding);
        var failing = binding is null ? null : FailingScript(binding, status);
        // Feedback for one of this binding's actions only (a mode change replaced the binding).
        var fb = feedback is not null && feedback.Step is >= 0 and < FeedbackSteps && binding is not null
                 && binding.All().Any(a => ReferenceEquals(a, feedback.Action)) ? feedback : null;
        if (fb is { ExpectFrom: { } from, Expect: { } expect })
            status = new ExpectingStatus(status ?? ActionStatus.None, from, expect);
        var announcing = announceStep is >= 0 and < AnnounceSteps && binding is not null;
        return kind switch
        {
            IndicatorKind.Led when announcing && BlinksOnModeChange(binding!) => new LedDisplay(Bit(announceStep % 2 == 0)),
            IndicatorKind.Led or IndicatorKind.LedPair when fb?.Error is not null => new LedDisplay(Bit(fb.Step % 2 == 0)),
            IndicatorKind.Led or IndicatorKind.LedPair when failing is not null => new LedDisplay(LedValue(failing, kind, config, modeId, audio, status)),
            IndicatorKind.Led or IndicatorKind.LedPair when fb is { Secondary: true } => new LedDisplay(Bit(fb.Step / 3 % 2 == 0)),
            IndicatorKind.Led or IndicatorKind.LedPair => new LedDisplay(LedValue(primary, kind, config, modeId, audio, status)),
            IndicatorKind.Screen when announcing && FlashesModeName(binding!) => new ScreenDisplay(LiveModeScreen(config, modeId)),
            IndicatorKind.Screen when fb?.Error is { } why => new ScreenDisplay(ErrorScreen(fb.Action, why, status)),
            IndicatorKind.Screen when failing is not null => new ScreenDisplay(Screen(failing, config, modeId, audio, status)),
            IndicatorKind.Screen when fb is { Secondary: true } => new ScreenDisplay(Screen(fb.Action, config, modeId, audio, status)),
            IndicatorKind.Screen => new ScreenDisplay(Screen(primary, config, modeId, audio, status)),
            _ => null,
        };
    }

    /// <summary>A press that did nothing: the error icon, why on top, and what was pressed below.</summary>
    private static ScreenContent ErrorScreen(ButtonActionConfig action, string why, IActionStatus? status)
    {
        string name;
        if (ButtonActionTypes.IsMedia(action.Action))
            name = (status ?? ActionStatus.None).Media(action.MediaApp).Name is { Length: > 0 } app ? app : MediaName;
        else if (ButtonActionTypes.RunsCode(action.Action))
            name = CommandPath.DisplayName(action.Command);
        else
            name = "";
        return ScreenContent.Text(why, ScreenIcon.Error, false, name);
    }

    /// <summary>A Play/pause's expected state, while the app still reports the one it had before
    /// the press (Windows takes a moment, and longer for a pause than for a play).</summary>
    private sealed class ExpectingStatus : IActionStatus
    {
        private readonly IActionStatus _inner;
        private readonly MediaPlayback _from;
        private readonly MediaPlayback _expect;

        public ExpectingStatus(IActionStatus inner, MediaPlayback from, MediaPlayback expect)
        {
            _inner = inner;
            _from = from;
            _expect = expect;
        }

        public MediaStatus Media(MediaAppConfig? target)
        {
            var real = _inner.Media(target);
            return real.Present && real.Playback == _from ? real with { Playback = _expect } : real;
        }

        public ProgramState Program(CommandConfig? command) => _inner.Program(command);
        public Scripts.ScriptStatus Script(ButtonActionConfig action) => _inner.Script(action);
    }

    /// <summary>Whether a press of <paramref name="pressed"/> makes the screen show it for a
    /// moment: one of the binding's actions (the same object), not its first one, and neither
    /// "Do nothing" nor a mode action (a mode change is announced on its own).</summary>
    public static bool ShowsWhenPressed(ButtonBindingConfig binding, ButtonActionConfig pressed)
        => pressed.Action != ButtonActionType.None && !IsModeAction(pressed)
           && !ReferenceEquals(pressed, Primary(binding))
           && binding.All().Any(a => ReferenceEquals(a, pressed));

    /// <summary>A Run a script action of the binding that is in its failure flash, or null.</summary>
    private static ButtonActionConfig? FailingScript(ButtonBindingConfig binding, IActionStatus? status)
        => status is null ? null
           : binding.All().FirstOrDefault(a => a.Action == ButtonActionType.RunScript && status.Script(a).FlashStep is not null);

    /// <summary>A single LED blinks after a mode change when the button has a mode action,
    /// unless its light follows "Go to mode" (that light already says whether its mode is live).</summary>
    public static bool BlinksOnModeChange(ButtonBindingConfig binding)
        => HasModeAction(binding) && Primary(binding)?.Action != ButtonActionType.GoToMode;

    /// <summary>A screen flashes the live mode after a mode change when the button has a mode
    /// action but its screen shows another action.</summary>
    public static bool FlashesModeName(ButtonBindingConfig binding)
        => HasModeAction(binding) && !IsModeAction(Primary(binding));

    private static bool HasModeAction(ButtonBindingConfig binding) => binding.All().Any(IsModeAction);

    private static bool IsModeAction(ButtonActionConfig? a)
        => a?.Action is ButtonActionType.NextMode or ButtonActionType.GoToMode;

    private static ButtonActionConfig? Primary(ButtonBindingConfig binding)
        => binding.All().FirstOrDefault(a => a.Action != ButtonActionType.None);

    /// <summary>The LED value for a whole binding (used by the settings UI's light preview).</summary>
    public static byte LedValueFor(ButtonBindingConfig binding, IndicatorKind kind, AppConfig config, string modeId, IAudioEngine audio,
                                   IActionStatus? status = null)
        => LedValue(Primary(binding), kind, config, modeId, audio, status);

    /// <summary>The steady LED value. 0 = off; mute: 1 while muted; switch: 1 while active.
    /// An LED pair: toggle 1 = A, 2 = B; modes 1 + the live mode's position. A single LED:
    /// toggle lit on B, dark on A; "Go to mode" lit while that mode is live; "Next mode" dark
    /// (it blinks on a change instead). Media: on while the target plays (Play/Pause, Stop). Program:
    /// on while it runs. Script: on while it runs, blinking during the failure flash.</summary>
    public static byte LedValue(ButtonActionConfig? action, IndicatorKind kind, AppConfig config, string modeId, IAudioEngine audio,
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
            case ButtonActionType.NextMode when single:
                return 0;
            case ButtonActionType.GoToMode when single:
                return Bit(action.ModeId == modeId);
            case ButtonActionType.NextMode:
            case ButtonActionType.GoToMode:
            {
                var index = config.Modes.FindIndex(p => p.Id == modeId);
                return index < 0 ? (byte)0 : (byte)Math.Min(255, index + 1);
            }
            default:
                return 0;
        }
    }

    /// <summary>Layout 1 content for an action (spec §2.2's table; custom-actions spec §3.3–§5.4):
    /// the top line says the state, the main line names the device, app, program or script.</summary>
    public static ScreenContent Screen(ButtonActionConfig? action, AppConfig config, string modeId, IAudioEngine audio,
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
                    MediaPlayback.Playing => ScreenContent.Text(Playing, ScreenIcon.Equalizer, false, name),
                    MediaPlayback.Paused => ScreenContent.Text(Paused, ScreenIcon.EqualizerStill, false, name),
                    _ => ScreenContent.Text(Stopped, ScreenIcon.EqualizerStill, false, name),
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
            case ButtonActionType.NextMode:
                return LiveModeScreen(config, modeId);
            case ButtonActionType.GoToMode:
                return ModeScreen(config, modeId, config.Modes.Find(p => p.Id == action.ModeId)?.Name ?? ChooseMode);
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

    private static ScreenContent LiveModeScreen(AppConfig config, string modeId)
        => ModeScreen(config, modeId, config.Modes.Find(p => p.Id == modeId)?.Name ?? "");

    private static ScreenContent ModeScreen(AppConfig config, string modeId, string main)
    {
        var count = config.Modes.Count;
        var index = Math.Max(0, config.Modes.FindIndex(p => p.Id == modeId));
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
