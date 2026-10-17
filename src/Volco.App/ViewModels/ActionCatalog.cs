using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Programs;

namespace Volco.App.ViewModels;

/// <summary>One entry of a button's action picker: a group header, or an action.</summary>
public sealed record ActionChoice(ButtonActionType? Type, string Name, bool IsHeader = false, bool IsAvailable = true)
{
    /// <summary>False for group headers and for actions that are not available yet.</summary>
    public bool IsSelectable => !IsHeader && IsAvailable && Type.HasValue;

    public override string ToString() => Name;
}

/// <summary>Which custom actions this platform can run (null services in <c>PlatformServices</c>
/// make theirs unavailable in the picker).</summary>
public sealed record ActionAvailability(bool Media, bool Programs, bool Scripts)
{
    public static ActionAvailability All { get; } = new(true, true, true);

    public bool IsAvailable(ButtonActionType t)
        => ButtonActionTypes.IsMedia(t) ? Media
           : t == ButtonActionType.OpenProgram ? Programs
           : t == ButtonActionType.RunScript ? Scripts
           : true;
}

/// <summary>A preset, as offered by the "Switch to preset…" picker.</summary>
public sealed record PresetChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Everything the settings UI knows about button actions: the grouped picker, short labels for
/// tiles and rows, which parameters an action takes, and what the controller light shows.
/// New action types (custom actions) are added here, and the pickers follow.
/// </summary>
public static class ActionCatalog
{
    public static IReadOnlyList<ActionChoice> Choices { get; } = new ActionChoice[]
    {
        new(null, "Audio", IsHeader: true),
        new(ButtonActionType.None, "Do nothing"),
        new(ButtonActionType.MuteCurrentOutput, "Mute current output"),
        new(ButtonActionType.MuteCurrentInput, "Mute current input (mic)"),
        new(ButtonActionType.MuteSpecificDevice, "Mute a specific device"),
        new(ButtonActionType.SetOutputDevice, "Switch output to…"),
        new(ButtonActionType.ToggleOutputDevice, "Toggle between two outputs"),
        new(null, "Presets", IsHeader: true),
        new(ButtonActionType.NextPreset, "Next preset"),
        new(ButtonActionType.GoToPreset, "Switch to preset…"),
        new(null, "Media", IsHeader: true),
        new(ButtonActionType.MediaPlayPause, "Play / pause"),
        new(ButtonActionType.MediaNext, "Next track"),
        new(ButtonActionType.MediaPrevious, "Previous track"),
        new(ButtonActionType.MediaStop, "Stop"),
        new(null, "Programs and scripts", IsHeader: true),
        new(ButtonActionType.OpenProgram, "Open a program"),
        new(ButtonActionType.RunScript, "Run a script"),
    };

    /// <summary><see cref="Choices"/>, with the actions this platform cannot run disabled.</summary>
    public static IReadOnlyList<ActionChoice> ChoicesFor(ActionAvailability availability)
        => availability == ActionAvailability.All
            ? Choices
            : Choices.Select(c => c.Type is { } t && !availability.IsAvailable(t) ? c with { IsAvailable = false } : c).ToList();

    public static ActionChoice For(ButtonActionType type)
        => Choices.FirstOrDefault(c => c.Type == type && !c.IsHeader) ?? Choices[1];

    /// <summary>The entry for <paramref name="type"/> in <paramref name="choices"/>.</summary>
    public static ActionChoice For(ButtonActionType type, IReadOnlyList<ActionChoice> choices)
        => choices.FirstOrDefault(c => c.Type == type && !c.IsHeader) ?? choices[1];

    public static bool NeedsDevice(ButtonActionType t) => t is ButtonActionType.MuteSpecificDevice or ButtonActionType.SetOutputDevice;
    public static bool NeedsPair(ButtonActionType t) => t is ButtonActionType.ToggleOutputDevice;
    public static bool NeedsPreset(ButtonActionType t) => t is ButtonActionType.GoToPreset;
    public static bool NeedsMediaApp(ButtonActionType t) => ButtonActionTypes.IsMedia(t);
    public static bool NeedsCommand(ButtonActionType t) => ButtonActionTypes.RunsCode(t);
    public static bool NeedsWhileRunning(ButtonActionType t) => t is ButtonActionType.RunScript;

    /// <summary>The action that decides the light: the first action that isn't None, single → double → long.</summary>
    public static (ButtonActionConfig Action, string Slot)? LightSource(ButtonBindingConfig binding)
    {
        if (binding.SinglePress.Action != ButtonActionType.None) return (binding.SinglePress, "Press");
        if (binding.DoublePress.Action != ButtonActionType.None) return (binding.DoublePress, "Double press");
        if (binding.LongPress.Action != ButtonActionType.None) return (binding.LongPress, "Hold");
        return null;
    }

    /// <summary>What a button with a screen shows.</summary>
    public static string ScreenDescription(ButtonBindingConfig binding)
    {
        if (LightSource(binding) is not { } source) return "Blank. None of this button’s actions is set in this preset.";
        var text = $"Shows the {source.Slot} action.";
        if (ControllerDisplay.FlashesPresetName(binding) && PresetSlot(binding) is { } slot)
            text += $" Flashes the preset’s name when {slot} changes the preset.";
        if (binding.All().Any(a => ControllerDisplay.ShowsWhenPressed(binding, a)))
            text += " Shows another action for a moment after you use it.";
        return text;
    }

    /// <summary>What a button's light shows, for one LED or an LED pair.</summary>
    public static string LightDescription(ButtonBindingConfig binding, bool singleLed,
                                          Func<string?, string?> presetName, Func<DeviceRefConfig?, string>? deviceName = null)
    {
        if (LightSource(binding) is not { } source)
            return "No light. None of this button’s actions has an on/off state.";
        var name = deviceName ?? DeviceShort;
        var a = source.Action;
        var what = a.Action switch
        {
            ButtonActionType.ToggleOutputDevice when singleLed => $"Dark on {name(a.Device)} (A), lit on {name(a.DeviceB)} (B).",
            ButtonActionType.ToggleOutputDevice => $"A lights for {name(a.Device)}, B for {name(a.DeviceB)}.",
            ButtonActionType.NextPreset when singleLed => "Dark; blinks when the preset changes.",
            ButtonActionType.GoToPreset when singleLed => $"On while {presetName(a.PresetId) ?? "that preset"} is live.",
            ButtonActionType.NextPreset or ButtonActionType.GoToPreset => "Shows the live preset: the first lights A, the second B.",
            ButtonActionType.SetOutputDevice => "On while that device is the output.",
            ButtonActionType.MediaPlayPause or ButtonActionType.MediaStop => $"On while {MediaTarget(a)} plays.",
            ButtonActionType.MediaNext or ButtonActionType.MediaPrevious => "Dark; next and previous have no on/off state.",
            ButtonActionType.OpenProgram when ProgramOpener.IsTrackable(a.Command) => $"On while {CommandName(a, "the program")} runs.",
            ButtonActionType.OpenProgram => "Dark; Volco can only tell whether a program (an .exe or its shortcut) runs.",
            ButtonActionType.RunScript => "On while the script runs; blinks when it fails.",
            _ => "On while muted.",
        };
        var text = $"{what} Follows the {source.Slot} action.";
        if (singleLed && a.Action != ButtonActionType.NextPreset && ControllerDisplay.BlinksOnPresetChange(binding))
            text += $" Blinks when {PresetSlot(binding)} changes the preset.";
        if (binding.All().Any(x => ControllerDisplay.ShowsWhenPressed(binding, x)))
            text += " Blinks twice when you use another of its actions.";
        return text;
    }

    /// <summary>The first slot with a preset action, or null.</summary>
    private static string? PresetSlot(ButtonBindingConfig binding)
    {
        static bool IsPreset(ButtonActionConfig a) => a.Action is ButtonActionType.NextPreset or ButtonActionType.GoToPreset;
        if (IsPreset(binding.SinglePress)) return "Press";
        if (IsPreset(binding.DoublePress)) return "Double press";
        if (IsPreset(binding.LongPress)) return "Hold";
        return null;
    }

    /// <summary>A short label for tiles and inspector rows, e.g. "Mute mic" or "Toggle Speakers / Headphones".</summary>
    public static string ShortLabel(ButtonActionConfig a, Func<string?, string?> presetName,
                                    Func<DeviceRefConfig?, string>? deviceName = null)
    {
        var name = deviceName ?? DeviceShort;
        return a.Action switch
        {
            ButtonActionType.None => "Nothing",
            ButtonActionType.MuteCurrentOutput => "Mute output",
            ButtonActionType.MuteCurrentInput => "Mute mic",
            ButtonActionType.MuteSpecificDevice => $"Mute {name(a.Device)}",
            ButtonActionType.SetOutputDevice => $"Switch to {name(a.Device)}",
            ButtonActionType.ToggleOutputDevice => $"Toggle {name(a.Device)} / {name(a.DeviceB)}",
            ButtonActionType.NextPreset => "Next preset",
            ButtonActionType.GoToPreset => $"Go to {presetName(a.PresetId) ?? "a preset"}",
            ButtonActionType.MediaPlayPause => WithApp("Play/pause", a),
            ButtonActionType.MediaNext => WithApp("Next track", a),
            ButtonActionType.MediaPrevious => WithApp("Previous track", a),
            ButtonActionType.MediaStop => WithApp("Stop", a),
            ButtonActionType.OpenProgram => $"Open {CommandName(a, "a program")}",
            ButtonActionType.RunScript => $"Run {CommandName(a, "a script")}",
            _ => a.Action.ToString(),
        };
    }

    private static string WithApp(string verb, ButtonActionConfig a)
        => a.MediaApp is { Name.Length: > 0 } app ? $"{verb} {app.Name}" : verb;

    private static string MediaTarget(ButtonActionConfig a)
        => a.MediaApp is { Name.Length: > 0 } app ? app.Name : "the current app";

    private static string CommandName(ButtonActionConfig a, string fallback)
        => CommandPath.DisplayName(a.Command) is { Length: > 0 } name ? name : fallback;

    /// <summary>"Speakers (Realtek Audio)" → "Speakers"; "?" when unset.</summary>
    public static string DeviceShort(DeviceRefConfig? d)
    {
        var name = d?.FriendlyName;
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var paren = name.IndexOf(" (", StringComparison.Ordinal);
        return paren > 0 ? name[..paren] : name;
    }
}
