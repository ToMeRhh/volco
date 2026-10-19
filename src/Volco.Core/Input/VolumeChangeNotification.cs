using Volco.Core.Communication;

namespace Volco.Core.Input;

/// <summary>What produced an OSD notification.</summary>
/// <remarks>
/// <see cref="Volume"/>: a slider moved (level = the new slider level; muted = the mute flag of
/// the device that governs it, see SliderProcessor.MuteSourceFor; icon = the slider's own device,
/// else the current output's; the OSD still shows the percentage, with the icon struck through and
/// a dimmed bar). <see cref="Mute"/>: a mute button was
/// pressed (the notification carries the target's real level and icon; the OSD shows "Muted"
/// while muted, the percentage otherwise). <see cref="Device"/>: a button switched the default output
/// device (label = the new default's display name, icon = its icon, level/muted = that device's state). <see cref="Mode"/>: a
/// button switched the live mode (label = the new mode's name; level and muted unused).
/// </remarks>
public enum OsdKind { Volume, Mute, Device, Mode }

/// <summary>
/// Platform-neutral payload describing a volume/mute/output-device change that the OSD flyout
/// should display. Carries only primitive data so it can be raised from the
/// cross-platform input layer without referencing Windows UI types.
/// <see cref="Icon"/> is a device's icon (chosen icon, else form factor) on a
/// <see cref="OsdKind.Device"/>, <see cref="OsdKind.Mute"/> or <see cref="OsdKind.Volume"/>
/// notification, null otherwise or
/// when the device is unknown. <see cref="Apps"/> is an apps slider's apps (exe names) on a
/// <see cref="OsdKind.Volume"/> notification, so the OSD can show an app's icon; null otherwise.
/// </summary>
public sealed record VolumeChangeNotification(
    string Label,
    float Level,
    bool Muted,
    OsdKind Kind,
    ScreenIcon? Icon = null,
    IReadOnlyList<string>? Apps = null);
