using Volco.Core.Input;

namespace Volco.App.Osd;

/// <summary>Which icon the OSD shows.</summary>
internal enum OsdGlyph { Volume, Muted, Speaker, Preset }

/// <summary>
/// Pure presentation state of the volume OSD (no Avalonia types, so plain [Fact]-testable).
/// Port of the label/glyph/percentage rules in the WinUI VolumeOsdWindow.ShowCore.
/// </summary>
internal sealed class VolumeOsdViewModel
{
    public string Label { get; private set; } = "Output";

    /// <summary>0 to 100, for the level bar.</summary>
    public double Level { get; private set; }

    /// <summary>"37%", or "Muted" for a muted Mute/Device notification (unmuted shows the percentage).</summary>
    public string ValueText { get; private set; } = "0%";

    /// <summary>The icon: muted whenever the target is muted, else speaker for an output-device
    /// switch, else the volume glyph.</summary>
    public OsdGlyph Glyph { get; private set; } = OsdGlyph.Volume;

    /// <summary>False for a preset switch, which has no level to show.</summary>
    public bool ShowsLevel => Glyph != OsdGlyph.Preset;

    /// <summary>Level bar opacity: dimmed while muted.</summary>
    public double LevelBarOpacity { get; private set; } = 1.0;

    /// <summary>Applies a notification. Returns true when anything visible changed.</summary>
    public bool Apply(VolumeChangeNotification n)
    {
        var level = Math.Clamp(n.Level * 100.0, 0, 100);

        var valueText = n.Kind == OsdKind.Preset ? ""
            : (n.Kind is OsdKind.Mute or OsdKind.Device) && n.Muted
            ? "Muted"
            : $"{Math.Round(level, MidpointRounding.AwayFromZero):0}%";
        // A preset switch has no level: the preset's name, "Preset" on the right, and no bar.
        var label = n.Label;
        if (n.Kind == OsdKind.Preset) valueText = "Preset";

        var glyph = n.Kind == OsdKind.Preset ? OsdGlyph.Preset
            : n.Muted ? OsdGlyph.Muted
            : n.Kind == OsdKind.Device ? OsdGlyph.Speaker
            : OsdGlyph.Volume;
        var barOpacity = n.Muted ? 0.4 : 1.0;

        var changed = Label != label || Level != level || ValueText != valueText
                      || Glyph != glyph || LevelBarOpacity != barOpacity;
        Label = label;
        Level = level;
        ValueText = valueText;
        Glyph = glyph;
        LevelBarOpacity = barOpacity;
        return changed;
    }
}
