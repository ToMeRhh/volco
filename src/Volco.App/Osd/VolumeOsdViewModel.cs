using Volco.Core.Input;

namespace Volco.App.Osd;

/// <summary>
/// Pure presentation state of the volume OSD (no Avalonia types, so plain [Fact]-testable).
/// Port of the label/glyph/percentage rules in the WinUI VolumeOsdWindow.ShowCore.
/// </summary>
internal sealed class VolumeOsdViewModel
{
    public string Label { get; private set; } = "Output";

    /// <summary>0 to 100, for the level bar.</summary>
    public double Level { get; private set; }

    /// <summary>"37%", or "Muted" for a muted mute-button notification (unmuted shows the percentage).</summary>
    public string ValueText { get; private set; } = "0%";

    /// <summary>True when the muted glyph (not the volume glyph) is shown.</summary>
    public bool IsMutedGlyph { get; private set; }

    /// <summary>Level bar opacity: dimmed while muted.</summary>
    public double LevelBarOpacity { get; private set; } = 1.0;

    /// <summary>Applies a notification. Returns true when anything visible changed.</summary>
    public bool Apply(VolumeChangeNotification n)
    {
        var level = Math.Clamp(n.Level * 100.0, 0, 100);

        var valueText = n.Kind == OsdKind.Mute && n.Muted
            ? "Muted"
            : $"{Math.Round(level, MidpointRounding.AwayFromZero):0}%";

        var mutedGlyph = n.Muted;
        var barOpacity = n.Muted ? 0.4 : 1.0;

        var changed = Label != n.Label || Level != level || ValueText != valueText
                      || IsMutedGlyph != mutedGlyph || LevelBarOpacity != barOpacity;
        Label = n.Label;
        Level = level;
        ValueText = valueText;
        IsMutedGlyph = mutedGlyph;
        LevelBarOpacity = barOpacity;
        return changed;
    }
}
