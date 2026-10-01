namespace Volco.Core.Input;

/// <summary>What produced an OSD notification.</summary>
/// <remarks>
/// <see cref="Volume"/>: a slider moved. <see cref="Mute"/>: a mute button was pressed (the
/// notification carries the target's real level; the OSD shows "Muted" while muted, the
/// percentage otherwise). <see cref="Device"/>: a button switched the default output device
/// (label = the new default's name, level/muted = that device's state).
/// </remarks>
public enum OsdKind { Volume, Mute, Device }

/// <summary>
/// Platform-neutral payload describing a volume/mute/output-device change that the OSD flyout
/// should display. Carries only primitive data so it can be raised from the
/// cross-platform input layer without referencing Windows UI types.
/// </summary>
public sealed record VolumeChangeNotification(
    string Label,
    float Level,
    bool Muted,
    OsdKind Kind);
