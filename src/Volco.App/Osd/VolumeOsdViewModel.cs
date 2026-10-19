using Volco.Core.Communication;
using Volco.Core.Input;

namespace Volco.App.Osd;

/// <summary>Which icon the OSD shows. <see cref="Device"/> is a device's own icon
/// (<see cref="VolumeOsdViewModel.DeviceIcon"/>).</summary>
internal enum OsdGlyph { Volume, Muted, Speaker, Mode, Device }

/// <summary>
/// Pure presentation state of the volume OSD (no Avalonia types, so plain [Fact]-testable).
/// Port of the label/glyph/percentage rules in the WinUI VolumeOsdWindow.ShowCore.
/// </summary>
internal sealed class VolumeOsdViewModel
{
    public string Label { get; private set; } = "Output";

    /// <summary>0 to 100, for the level bar.</summary>
    public double Level { get; private set; }

    /// <summary>"37%", or "Muted" for a muted Mute/Device notification. A slider drag (Volume)
    /// always shows the percentage, even while muted - the glyph and dimmed bar show the mute.</summary>
    public string ValueText { get; private set; } = "0%";

    /// <summary>The icon. An output-device switch, a mute press or a slider drag shows the
    /// notification's device icon (the new device; the muted device; the slider's device, or the
    /// current output for an app slider), dimmed and struck through while muted (see
    /// <see cref="DeviceIconMuted"/>). Without an icon: the muted glyph whenever the target is
    /// muted, else the speaker glyph for a switch and the volume glyph otherwise.</summary>
    public OsdGlyph Glyph { get; private set; } = OsdGlyph.Volume;

    /// <summary>The device icon shown when <see cref="Glyph"/> is <see cref="OsdGlyph.Device"/>, else null.</summary>
    public ScreenIcon? DeviceIcon { get; private set; }

    /// <summary>True when the device icon is shown for a muted device: the view dims it and
    /// draws a slash over it.</summary>
    public bool DeviceIconMuted => Glyph == OsdGlyph.Device && Muted;

    /// <summary>The target is muted (the device icon or app icon is dimmed and struck through).</summary>
    public bool Muted { get; private set; }

    /// <summary>An apps slider's apps: the window shows the first one's icon when it has one, in
    /// place of <see cref="Glyph"/>. Empty for everything else.</summary>
    public IReadOnlyList<string> Apps { get; private set; } = Array.Empty<string>();

    /// <summary>False for a mode switch, which has no level to show.</summary>
    public bool ShowsLevel => Glyph != OsdGlyph.Mode;

    /// <summary>Level bar opacity: dimmed while muted.</summary>
    public double LevelBarOpacity { get; private set; } = 1.0;

    /// <summary>Applies a notification. Returns true when anything visible changed.</summary>
    public bool Apply(VolumeChangeNotification n)
    {
        var level = Math.Clamp(n.Level * 100.0, 0, 100);

        var valueText = n.Kind == OsdKind.Mode ? ""
            : (n.Kind is OsdKind.Mute or OsdKind.Device) && n.Muted
            ? "Muted"
            : $"{Math.Round(level, MidpointRounding.AwayFromZero):0}%";
        // A mode switch has no level: the mode's name, "Mode" on the right, and no bar.
        var label = n.Label;
        if (n.Kind == OsdKind.Mode) valueText = "Mode";

        var deviceIcon = n.Kind is OsdKind.Device or OsdKind.Mute or OsdKind.Volume ? n.Icon : null;
        var glyph = n.Kind == OsdKind.Mode ? OsdGlyph.Mode
            : deviceIcon is not null ? OsdGlyph.Device
            : n.Muted ? OsdGlyph.Muted
            : n.Kind == OsdKind.Device ? OsdGlyph.Speaker
            : OsdGlyph.Volume;
        var barOpacity = n.Muted ? 0.4 : 1.0;
        var apps = n.Kind == OsdKind.Volume ? n.Apps ?? Array.Empty<string>() : Array.Empty<string>();

        var changed = Label != label || Level != level || ValueText != valueText
                      || Glyph != glyph || DeviceIcon != deviceIcon || Muted != n.Muted
                      || LevelBarOpacity != barOpacity || !Apps.SequenceEqual(apps);
        Label = label;
        Level = level;
        ValueText = valueText;
        Glyph = glyph;
        DeviceIcon = deviceIcon;
        Muted = n.Muted;
        LevelBarOpacity = barOpacity;
        Apps = apps;
        return changed;
    }
}
