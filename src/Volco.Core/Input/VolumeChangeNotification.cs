namespace Volco.Core.Input;

public enum OsdKind { Volume, Mute }

/// <summary>
/// Platform-neutral payload describing a volume/mute change that the OSD flyout
/// should display. Carries only primitive data so it can be raised from the
/// cross-platform input layer without referencing Windows UI types.
/// </summary>
public sealed record VolumeChangeNotification(
    string Label,
    float Level,
    bool Muted,
    OsdKind Kind);
