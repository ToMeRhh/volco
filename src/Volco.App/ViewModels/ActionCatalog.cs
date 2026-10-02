using Volco.Core.Config;

namespace Volco.App.ViewModels;

/// <summary>One entry of a button's action picker: a group header, or an action.</summary>
public sealed record ActionChoice(ButtonActionType? Type, string Name, bool IsHeader = false, bool IsAvailable = true)
{
    /// <summary>False for group headers and for actions that are not available yet.</summary>
    public bool IsSelectable => !IsHeader && IsAvailable && Type.HasValue;

    public override string ToString() => Name;
}

/// <summary>A preset, as offered by the "Switch to preset…" picker.</summary>
public sealed record PresetChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Everything the settings UI knows about button actions: the grouped picker, short labels for
/// card summaries, which parameters an action takes, and what the controller light shows.
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
        new(null, "Custom", IsHeader: true),
        new(null, "Run a program, call a webhook… (coming later)", IsAvailable: false),
    };

    public static ActionChoice For(ButtonActionType type)
        => Choices.FirstOrDefault(c => c.Type == type && !c.IsHeader) ?? Choices[1];

    public static bool NeedsDevice(ButtonActionType t) => t is ButtonActionType.MuteSpecificDevice or ButtonActionType.SetOutputDevice;
    public static bool NeedsPair(ButtonActionType t) => t is ButtonActionType.ToggleOutputDevice;
    public static bool NeedsPreset(ButtonActionType t) => t is ButtonActionType.GoToPreset;

    /// <summary>The action that decides the light: the first action that isn't None, single → double → long.</summary>
    public static (ButtonActionConfig Action, string Slot)? LightSource(ButtonBindingConfig binding)
    {
        if (binding.SinglePress.Action != ButtonActionType.None) return (binding.SinglePress, "Press");
        if (binding.DoublePress.Action != ButtonActionType.None) return (binding.DoublePress, "Double press");
        if (binding.LongPress.Action != ButtonActionType.None) return (binding.LongPress, "Hold");
        return null;
    }

    /// <summary>The "Screen: …" line on a button card with a screen.</summary>
    public static string ScreenDescription(ButtonBindingConfig binding)
    {
        if (binding.SinglePress.Action != ButtonActionType.None) return "Shows the Press action.";
        if (binding.DoublePress.Action != ButtonActionType.None) return "Shows the Double press action.";
        if (binding.LongPress.Action != ButtonActionType.None) return "Shows the Hold action.";
        return "Blank. None of this button’s actions is set in this preset.";
    }

    /// <summary>The "Controller light: …" line on a button card.</summary>
    public static string LightDescription(ButtonBindingConfig binding)
    {
        if (LightSource(binding) is not { } source)
            return "No light. None of this button’s actions has an on/off state.";
        var follows = $"Follows the {source.Slot} action.";
        return source.Action.Action switch
        {
            ButtonActionType.ToggleOutputDevice => $"Shows which output is active (A or B). {follows}",
            ButtonActionType.NextPreset or ButtonActionType.GoToPreset => $"Shows which preset is live. {follows}",
            ButtonActionType.SetOutputDevice => $"On while that device is the output. {follows}",
            _ => $"On while muted. {follows}",
        };
    }

    /// <summary>A short label for card summaries, e.g. "Mute mic" or "Toggle Speakers / Headphones".</summary>
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
            _ => a.Action.ToString(),
        };
    }

    /// <summary>The closed card's summary for one preset's binding.</summary>
    public static string Summary(ButtonBindingConfig b, Func<string?, string?> presetName,
                                 Func<DeviceRefConfig?, string>? deviceName = null)
    {
        var parts = new List<string>();
        if (b.SinglePress.Action != ButtonActionType.None) parts.Add(ShortLabel(b.SinglePress, presetName, deviceName));
        if (b.DoublePress.Action != ButtonActionType.None) parts.Add("Double: " + ShortLabel(b.DoublePress, presetName, deviceName));
        if (b.LongPress.Action != ButtonActionType.None) parts.Add("Hold: " + ShortLabel(b.LongPress, presetName, deviceName));
        return string.Join(" · ", parts);
    }

    /// <summary>"Speakers (Realtek Audio)" → "Speakers"; "?" when unset.</summary>
    public static string DeviceShort(DeviceRefConfig? d)
    {
        var name = d?.FriendlyName;
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var paren = name.IndexOf(" (", StringComparison.Ordinal);
        return paren > 0 ? name[..paren] : name;
    }
}
