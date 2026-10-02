namespace Volco.Core.Audio;

/// <summary>
/// Read helpers over <see cref="IAudioEngine.TryGetDeviceState"/>, so every caller (OSD,
/// controller indicators, sliders) treats a device that does not resolve the same way.
/// </summary>
public static class AudioEngineExtensions
{
    /// <summary>True only when the device resolves and is muted.</summary>
    public static bool IsMuted(this IAudioEngine audio, DeviceRef device)
        => audio.TryGetDeviceState(device, out _, out var muted) && muted;

    /// <summary>The device's level and mute flag; level 0 and not muted when it does not resolve.</summary>
    public static (float Level, bool Muted) GetStateOrDefault(this IAudioEngine audio, DeviceRef device)
        => audio.TryGetDeviceState(device, out var level, out var muted) ? (level, muted) : (0f, false);
}
