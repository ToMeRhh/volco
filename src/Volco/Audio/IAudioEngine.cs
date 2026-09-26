namespace Volco.Audio;

public interface IAudioEngine : IDisposable
{
    // Queries — served from the in-memory registry, never enumerate.
    IReadOnlyList<AudioDevice>      GetDevices(AudioFlow flow);
    AudioDevice?                    GetDefaultDevice(AudioFlow flow, AudioRole role);
    IReadOnlyList<AudioSessionInfo> GetSessions();

    // Writes — O(1) lookup plus one COM call, posted to the audio thread.
    void SetDeviceVolume(DeviceRef device, float level);
    void SetDeviceMute(DeviceRef device, bool muted);
    void ToggleDeviceMute(DeviceRef device);
    void SetSessionVolume(SessionFilter filter, float level);

    bool TryGetDeviceState(DeviceRef device, out float level, out bool muted);

    /// <summary>Sets the Windows default endpoint for all three roles.</summary>
    void SetDefaultDevice(string endpointId, AudioFlow flow);

    event EventHandler<DevicesChangedEventArgs>?      DevicesChanged;
    event EventHandler<SessionsChangedEventArgs>?     SessionsChanged;
    event EventHandler<DeviceVolumeChangedEventArgs>? DeviceVolumeChanged;
}
