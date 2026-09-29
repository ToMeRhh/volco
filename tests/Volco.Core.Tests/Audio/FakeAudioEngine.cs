using Volco.Core.Audio;

namespace Volco.Core.Tests.Audio;

/// <summary>
/// Recording in-memory <see cref="IAudioEngine"/> for tests. Synchronous: writes land in
/// the call lists immediately, with no thread hop, so tests need no waiting.
/// </summary>
public sealed class FakeAudioEngine : IAudioEngine
{
    public List<(DeviceRef Device, float Level)>  DeviceVolumeCalls  { get; } = new();
    public List<(DeviceRef Device, bool Muted)>   SetMuteCalls       { get; } = new();
    public List<DeviceRef>                        ToggleMuteCalls    { get; } = new();
    public List<(SessionFilter Filter, float Level)> SessionVolumeCalls { get; } = new();
    public List<(string EndpointId, AudioFlow Flow)> SetDefaultCalls { get; } = new();

    public List<AudioDevice>      Devices  { get; set; } = new();
    public List<AudioSessionInfo> Sessions { get; set; } = new();
    public AudioDevice? DefaultRender  { get; set; }
    public AudioDevice? DefaultCapture { get; set; }

    /// <summary>Endpoint id -> (level, muted). Seed this to drive TryGetDeviceState.</summary>
    public Dictionary<string, (float Level, bool Muted)> DeviceStates { get; } = new();

    public event EventHandler<DevicesChangedEventArgs>?      DevicesChanged;
    public event EventHandler<SessionsChangedEventArgs>?     SessionsChanged;
    public event EventHandler<DeviceVolumeChangedEventArgs>? DeviceVolumeChanged;

    public IReadOnlyList<AudioDevice> GetDevices(AudioFlow flow)
        => Devices.Where(d => d.Flow == flow).ToList();

    public AudioDevice? GetDefaultDevice(AudioFlow flow, AudioRole role)
        => flow == AudioFlow.Render ? DefaultRender : DefaultCapture;

    public IReadOnlyList<AudioSessionInfo> GetSessions() => Sessions;

    public void SetDeviceVolume(DeviceRef device, float level)
        => DeviceVolumeCalls.Add((device, level));

    public void SetDeviceMute(DeviceRef device, bool muted)
        => SetMuteCalls.Add((device, muted));

    public void ToggleDeviceMute(DeviceRef device)
        => ToggleMuteCalls.Add(device);

    public void SetSessionVolume(SessionFilter filter, float level)
        => SessionVolumeCalls.Add((filter, level));

    public void SetDefaultDevice(string endpointId, AudioFlow flow)
        => SetDefaultCalls.Add((endpointId, flow));

    public bool TryGetDeviceState(DeviceRef device, out float level, out bool muted)
    {
        var key = device.Kind == DeviceRefKind.Default
            ? (device.Flow == AudioFlow.Render ? DefaultRender?.Id : DefaultCapture?.Id) ?? ""
            : device.Id;

        if (DeviceStates.TryGetValue(key, out var state))
        {
            (level, muted) = state;
            return true;
        }
        level = 0f;
        muted = false;
        return false;
    }

    public void RaiseSessionCreated(AudioSessionInfo session)
        => SessionsChanged?.Invoke(this, new SessionsChangedEventArgs(SessionChangeKind.Created, session));

    public void RaiseSessionExpired(AudioSessionInfo session)
        => SessionsChanged?.Invoke(this, new SessionsChangedEventArgs(SessionChangeKind.Expired, session));

    public void RaiseDeviceChanged(DeviceChangeKind kind, AudioDevice device)
        => DevicesChanged?.Invoke(this, new DevicesChangedEventArgs(kind, device));

    public void RaiseDeviceVolumeChanged(string deviceId, float level, bool muted, bool isOwnChange)
        => DeviceVolumeChanged?.Invoke(this, new DeviceVolumeChangedEventArgs(deviceId, level, muted, isOwnChange));

    public void Dispose() { }
}
