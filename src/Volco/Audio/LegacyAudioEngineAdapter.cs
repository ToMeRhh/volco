namespace Volco.Audio;

/// <summary>
/// Transitional <see cref="IAudioEngine"/> implemented over the old name-based
/// <see cref="IAudioManager"/>, so the app keeps working while the seam lands.
/// Deleted once the real Core Audio engine replaces it.
///
/// Deliberately lossy: the legacy manager has no endpoint ids and no session identity,
/// so <see cref="DeviceRefKind.ById"/> degrades to "the default device" rather than
/// feeding an id into a friendly-name substring match, and the change events never fire.
/// </summary>
public sealed class LegacyAudioEngineAdapter : IAudioEngine
{
    private readonly IAudioManager _legacy;

    public LegacyAudioEngineAdapter(IAudioManager legacy) => _legacy = legacy;

#pragma warning disable 67 // The legacy manager has no notifications to forward.
    public event EventHandler<DevicesChangedEventArgs>?      DevicesChanged;
    public event EventHandler<SessionsChangedEventArgs>?     SessionsChanged;
    public event EventHandler<DeviceVolumeChangedEventArgs>? DeviceVolumeChanged;
#pragma warning restore 67

    private static string? ToLegacyName(DeviceRef device)
        => device.Kind == DeviceRefKind.ByFriendlyName && !string.IsNullOrEmpty(device.Id)
            ? device.Id
            : null;

    public IReadOnlyList<AudioDevice> GetDevices(AudioFlow flow)
    {
        var infos = flow == AudioFlow.Render ? _legacy.GetOutputDevices() : _legacy.GetInputDevices();
        return infos.Select(i => new AudioDevice(i.Id, i.Name, flow, false, false, false)).ToList();
    }

    public AudioDevice? GetDefaultDevice(AudioFlow flow, AudioRole role)
    {
        if (flow != AudioFlow.Render) return null;
        var name = _legacy.GetDefaultOutputDeviceName();
        return name == null ? null : new AudioDevice("", name, flow, true, true, true);
    }

    public IReadOnlyList<AudioSessionInfo> GetSessions()
        => _legacy.GetActiveProcessNames()
                  .Select(n => new AudioSessionInfo(n, 0, n, "", false))
                  .ToList();

    public void SetDeviceVolume(DeviceRef device, float level)
        => _legacy.SetDeviceVolume(level, ToLegacyName(device));

    public void SetDeviceMute(DeviceRef device, bool muted)
    {
        // The legacy manager only toggles. Read, then toggle if it disagrees.
        var current = _legacy.IsOutputMuted(ToLegacyName(device));
        if (current != muted) _legacy.ToggleMuteOutput(ToLegacyName(device));
    }

    public void ToggleDeviceMute(DeviceRef device)
    {
        if (device.Flow == AudioFlow.Capture) _legacy.ToggleMuteInput();
        else _legacy.ToggleMuteOutput(ToLegacyName(device));
    }

    public void SetSessionVolume(SessionFilter filter, float level)
    {
        switch (filter)
        {
            case SessionFilter.ByImageNames byNames:
                _legacy.SetProcessVolumes(byNames.Names, level);
                break;

            case SessionFilter.Unmapped unmapped:
                var mapped = new HashSet<string>(unmapped.MappedNames, StringComparer.OrdinalIgnoreCase);
                var targets = _legacy.GetActiveProcessNames()
                                     .Where(n => !mapped.Contains(n))
                                     .ToList();
                _legacy.SetProcessVolumes(targets, level);
                break;
        }
    }

    public bool TryGetDeviceState(DeviceRef device, out float level, out bool muted)
    {
        level = 0f;
        muted = device.Flow == AudioFlow.Capture
            ? _legacy.IsInputMuted()
            : _legacy.IsOutputMuted(ToLegacyName(device));
        return true;
    }

    public void SetDefaultDevice(string endpointId, AudioFlow flow)
        => _legacy.SetDefaultOutputDevice(endpointId);

    public void Dispose() { }
}
