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

/// <summary>How a button's controller light shows its value (spec §3.6).</summary>
public enum LightKind { None, OnOff, Pair, Preset }

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

    public static LightKind LightKindOf(ButtonActionType t) => t switch
    {
        ButtonActionType.MuteCurrentOutput or ButtonActionType.MuteCurrentInput
            or ButtonActionType.MuteSpecificDevice or ButtonActionType.SetOutputDevice => LightKind.OnOff,
        ButtonActionType.ToggleOutputDevice => LightKind.Pair,
        ButtonActionType.NextPreset or ButtonActionType.GoToPreset => LightKind.Preset,
        _ => LightKind.None,
    };

    /// <summary>The action that decides the light: the first stateful one, single → double → long
    /// (the same rule as <see cref="Volco.Core.Communication.ButtonFeedback"/>).</summary>
    public static (ButtonActionConfig Action, string Slot)? LightSource(ButtonBindingConfig binding)
    {
        if (LightKindOf(binding.SinglePress.Action) != LightKind.None) return (binding.SinglePress, "Press");
        if (LightKindOf(binding.DoublePress.Action) != LightKind.None) return (binding.DoublePress, "Double press");
        if (LightKindOf(binding.LongPress.Action) != LightKind.None) return (binding.LongPress, "Hold");
        return null;
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
    public static string ShortLabel(ButtonActionConfig a, Func<string?, string?> presetName) => a.Action switch
    {
        ButtonActionType.None => "Nothing",
        ButtonActionType.MuteCurrentOutput => "Mute output",
        ButtonActionType.MuteCurrentInput => "Mute mic",
        ButtonActionType.MuteSpecificDevice => $"Mute {DeviceShort(a.Device)}",
        ButtonActionType.SetOutputDevice => $"Switch to {DeviceShort(a.Device)}",
        ButtonActionType.ToggleOutputDevice => $"Toggle {DeviceShort(a.Device)} / {DeviceShort(a.DeviceB)}",
        ButtonActionType.NextPreset => "Next preset",
        ButtonActionType.GoToPreset => $"Go to {presetName(a.PresetId) ?? "a preset"}",
        _ => a.Action.ToString(),
    };

    /// <summary>The closed card's summary for one preset's binding.</summary>
    public static string Summary(ButtonBindingConfig b, Func<string?, string?> presetName)
    {
        var parts = new List<string>();
        if (b.SinglePress.Action != ButtonActionType.None) parts.Add(ShortLabel(b.SinglePress, presetName));
        if (b.DoublePress.Action != ButtonActionType.None) parts.Add("Double: " + ShortLabel(b.DoublePress, presetName));
        if (b.LongPress.Action != ButtonActionType.None) parts.Add("Hold: " + ShortLabel(b.LongPress, presetName));
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
