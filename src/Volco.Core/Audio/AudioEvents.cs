namespace Volco.Core.Audio;

public enum DeviceChangeKind { Added, Removed, StateChanged, DefaultChanged }

public enum SessionChangeKind { Created, Expired }

public sealed record DevicesChangedEventArgs(DeviceChangeKind Kind, AudioDevice Device);

public sealed record SessionsChangedEventArgs(SessionChangeKind Kind, AudioSessionInfo Session);

/// <summary>
/// An endpoint volume or mute change. <paramref name="IsOwnChange"/> is true when the
/// change carried this process's event-context GUID; consumers normally ignore those
/// to avoid feedback loops with the OSD and the controller LEDs.
/// </summary>
public sealed record DeviceVolumeChangedEventArgs(
    string DeviceId, float Level, bool Muted, bool IsOwnChange);
