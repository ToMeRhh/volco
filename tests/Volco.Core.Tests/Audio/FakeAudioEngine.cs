using Volco.Core.Audio;

namespace Volco.Core.Tests.Audio;

/// <summary>
/// Recording in-memory <see cref="IAudioEngine"/> for tests. Calls into this engine can come from
/// a runtime timer or audio-thread stand-in rather than the test thread, so the call-recording
/// lists below are written under a lock (see <see cref="Snapshot{T}"/>) even though nothing else
/// about this fake is asynchronous.
/// </summary>
public sealed class FakeAudioEngine : IAudioEngine
{
    /// <summary>Guards every Add below. Runtime timer/audio threads can be appending to these
    /// call-recording lists while a test thread reads them; <see cref="List{T}.Add"/> bumps its
    /// internal size before storing the item, so an unsynchronised reader that only waits on
    /// <c>Count &gt; 0</c> can observe a default-initialised element. Callers that read after a
    /// wait must go through <see cref="Snapshot{T}"/>, taken under this same lock, rather than
    /// indexing or enumerating these lists directly.</summary>
    private readonly object _lock = new();

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

    /// <summary>Session instance id -> peak. Seed this to drive GetSessionPeak.</summary>
    public Dictionary<string, float> Peaks { get; } = new();

    public event EventHandler<DevicesChangedEventArgs>?      DevicesChanged;
    public event EventHandler<SessionsChangedEventArgs>?     SessionsChanged;
    public event EventHandler<DeviceVolumeChangedEventArgs>? DeviceVolumeChanged;

    public IReadOnlyList<AudioDevice> GetDevices(AudioFlow flow)
        => Devices.Where(d => d.Flow == flow).ToList();

    public AudioDevice? GetDefaultDevice(AudioFlow flow)
        => flow == AudioFlow.Render ? DefaultRender : DefaultCapture;

    public IReadOnlyList<AudioSessionInfo> GetSessions() => Sessions;

    public float GetSessionPeak(string instanceId)
        => Peaks.TryGetValue(instanceId, out var peak) ? peak : 0f;

    public void SetDeviceVolume(DeviceRef device, float level)
    {
        lock (_lock) DeviceVolumeCalls.Add((device, level));
    }

    public void SetDeviceMute(DeviceRef device, bool muted)
    {
        lock (_lock) SetMuteCalls.Add((device, muted));
    }

    public void ToggleDeviceMute(DeviceRef device)
    {
        lock (_lock) ToggleMuteCalls.Add(device);
    }

    public void SetSessionVolume(SessionFilter filter, float level)
    {
        lock (_lock) SessionVolumeCalls.Add((filter, level));
    }

    public void SetDefaultDevice(string endpointId, AudioFlow flow)
    {
        lock (_lock) SetDefaultCalls.Add((endpointId, flow));
    }

    /// <summary>Thread-safe point-in-time copy of a call-recording list, taken under the same
    /// lock the Add methods above use. Tests that waited on a condition like <c>Count &gt; 0</c>
    /// must read the list's contents through this, not by indexing/enumerating it directly.</summary>
    public IReadOnlyList<T> Snapshot<T>(List<T> list)
    {
        lock (_lock) return list.ToArray();
    }

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

    /// <summary>True once <see cref="Dispose"/> has been called. Dispose has no other effect than the optional hook.</summary>
    public bool Disposed { get; private set; }

    /// <summary>Optional test hook, invoked from <see cref="Dispose"/> after <see cref="Disposed"/> is set.</summary>
    public Action? OnDispose { get; set; }

    /// <summary>How many handlers are currently subscribed to <see cref="DeviceVolumeChanged"/>.</summary>
    public int DeviceVolumeChangedSubscriberCount => DeviceVolumeChanged?.GetInvocationList().Length ?? 0;

    public void Dispose()
    {
        Disposed = true;
        OnDispose?.Invoke();
    }
}
