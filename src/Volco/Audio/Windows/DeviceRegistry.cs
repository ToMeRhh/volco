using System.Linq;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.Media.Audio.Endpoints;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Volco.Audio;

namespace Volco.Audio.Windows;

/// <summary>
/// Owns every live <see cref="IMMDevice"/> and its <see cref="IAudioEndpointVolume"/> /
/// <see cref="IAudioSessionManager2"/>. Built once at <see cref="Start"/>, kept current by
/// <see cref="IMMNotificationClient"/> notifications. Nothing outside this class calls
/// EnumerateAudioEndpoints/GetDefaultAudioEndpoint - that happens only here, at startup and
/// in response to a live notification (spec §2.3 invariant 1).
///
/// All public members must be called on the AudioEngine's audio thread (MTA). This class does
/// not create or own a thread; see the contract documented on <see cref="Start"/>.
/// </summary>
[ComVisible(true)]
internal sealed class DeviceRegistry : IDisposable, IMMNotificationClient
{
    /// <summary>One cached endpoint and the COM objects hanging off it. Created once, disposed once.</summary>
    internal sealed class DeviceHandle : IDisposable
    {
        public required string Id { get; init; }              // IMMDevice::GetId
        public required string FriendlyName { get; init; }    // PKEY_Device_FriendlyName
        public required AudioFlow Flow { get; init; }
        public required IMMDevice Device { get; init; }

        /// <summary>Created with the handle; its change callback keeps <see cref="CachedLevel"/>/<see cref="CachedMuted"/> fresh.</summary>
        public required IAudioEndpointVolume Volume { get; init; }

        /// <summary>Render devices only; null for capture endpoints.</summary>
        public IAudioSessionManager2? SessionManager { get; init; }

        /// <summary>Last known state, so TryGetDeviceState (Task 13) never blocks the audio thread on a COM call.</summary>
        public float CachedLevel { get; set; }
        public bool CachedMuted { get; set; }

        /// <summary>
        /// Not part of the fixed public contract Tasks 12-13 consume - internal bookkeeping so
        /// <see cref="Dispose"/> can unregister the per-device volume callback registered when
        /// this handle was built.
        /// </summary>
        internal IAudioEndpointVolumeCallback? VolumeCallback { get; set; }

        public void Dispose()
        {
            try
            {
                if (VolumeCallback is not null)
                {
                    Volume.UnregisterControlChangeNotify(VolumeCallback);
                }
            }
            catch (COMException)
            {
                // Device is likely already gone; nothing to unregister from.
            }

            ReleaseIfComObject(SessionManager);
            ReleaseIfComObject(Volume);
            ReleaseIfComObject(Device);
        }
    }

    /// <summary>
    /// Per-device sink for <see cref="IAudioEndpointVolume.RegisterControlChangeNotify"/>. Per the
    /// documented Core Audio contract (spec §2.1/§2.4) this must not block, must not call back into
    /// COM, and must not register/unregister notifications. It does exactly one thing: read the two
    /// fields it needs off the notification struct (valid only for the duration of this call) and
    /// write them onto the handle.
    /// </summary>
    [ComVisible(true)]
    private sealed unsafe class EndpointVolumeCallback : IAudioEndpointVolumeCallback
    {
        private readonly DeviceHandle _handle;

        public EndpointVolumeCallback(DeviceHandle handle) => _handle = handle;

        public void OnNotify(AUDIO_VOLUME_NOTIFICATION_DATA* pNotify)
        {
            if (pNotify is null)
            {
                return;
            }

            _handle.CachedLevel = pNotify->fMasterVolume;
            _handle.CachedMuted = pNotify->bMuted != 0;
        }
    }

    private readonly Action<DevicesChangedEventArgs> _onChanged;

    // Single lock protecting both dictionaries. Core Audio delivers IMMNotificationClient
    // callbacks on whatever MTA/RPC worker thread Windows Audio chooses - not necessarily the
    // audio thread that registered the client - so reads from GetDevices/GetDefault/Resolve
    // (called from the audio thread) and writes from a notification callback can race without it.
    // This is a short, uncontended, non-blocking critical section around a few COM calls; it does
    // not reintroduce the per-tick COM re-activation bug this class exists to fix, because reads
    // here are not a 50x/second hot path (that hot path is SessionRegistry's write path, Task 12).
    private readonly object _lock = new();
    private readonly Dictionary<string, DeviceHandle> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<(AudioFlow Flow, AudioRole Role), string> _defaults = new();

    private IMMDeviceEnumerator? _enumerator;
    private bool _started;
    private bool _disposed;

    public DeviceRegistry(Action<DevicesChangedEventArgs> onChanged)
    {
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
    }

    /// <summary>
    /// Enumerates every DEVICE_STATE_ACTIVE endpoint (both flows) exactly once, builds a
    /// <see cref="DeviceHandle"/> for each, resolves the three default roles for each flow, and
    /// only then registers this instance as the <see cref="IMMNotificationClient"/>.
    ///
    /// Threading contract: this method must run on an MTA thread (CoInitializeEx(COINIT_MULTITHREADED)
    /// already called on it). Registering IMMNotificationClient from an STA thread is a documented
    /// deadlock source (spec §2.1). DeviceRegistry does not create or own that thread - Task 13's
    /// AudioEngine does, and it must call Start() from its dedicated audio thread before any other
    /// member of this class is used.
    /// </summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;

        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();

        EnumerateFlow(EDataFlow.eRender);
        EnumerateFlow(EDataFlow.eCapture);

        CacheDefault(AudioFlow.Render, AudioRole.Console, EDataFlow.eRender, ERole.eConsole);
        CacheDefault(AudioFlow.Render, AudioRole.Multimedia, EDataFlow.eRender, ERole.eMultimedia);
        CacheDefault(AudioFlow.Render, AudioRole.Communications, EDataFlow.eRender, ERole.eCommunications);
        CacheDefault(AudioFlow.Capture, AudioRole.Console, EDataFlow.eCapture, ERole.eConsole);
        CacheDefault(AudioFlow.Capture, AudioRole.Multimedia, EDataFlow.eCapture, ERole.eMultimedia);
        CacheDefault(AudioFlow.Capture, AudioRole.Communications, EDataFlow.eCapture, ERole.eCommunications);

        // Registered only after the initial snapshot above exists - the enumeration and default
        // lookups that follow (in the notification callbacks) are the only other place this class
        // calls EnumerateAudioEndpoints/GetDefaultAudioEndpoint (spec §2.3 invariant 1).
        _enumerator.RegisterEndpointNotificationCallback(this);
    }

    public IReadOnlyList<AudioDevice> GetDevices(AudioFlow flow)
    {
        lock (_lock)
        {
            return _devices.Values.Where(h => h.Flow == flow).Select(ToAudioDeviceLocked).ToList();
        }
    }

    public AudioDevice? GetDefault(AudioFlow flow, AudioRole role)
    {
        lock (_lock)
        {
            return TryGetDefaultLocked(flow, role, out var device) ? device : null;
        }
    }

    /// <summary>
    /// Resolution order: exact id match, then exact friendly-name match (against the same payload
    /// string - see report for why <see cref="DeviceRef.Kind"/> doesn't gate this), then the
    /// default for the ref's flow/role. An unresolvable id/name falls through to the default;
    /// a <see cref="DeviceRefKind.Default"/> ref (whose payload is always "") goes straight there.
    /// </summary>
    public DeviceHandle? Resolve(DeviceRef reference)
    {
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(reference.Id))
            {
                if (_devices.TryGetValue(reference.Id, out var byId))
                {
                    return byId;
                }

                foreach (var handle in _devices.Values)
                {
                    if (string.Equals(handle.FriendlyName, reference.Id, StringComparison.Ordinal))
                    {
                        return handle;
                    }
                }
            }

            if (_defaults.TryGetValue((reference.Flow, reference.Role), out var defaultId)
                && !string.IsNullOrEmpty(defaultId)
                && _devices.TryGetValue(defaultId, out var byDefault))
            {
                return byDefault;
            }

            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_enumerator is not null)
        {
            try
            {
                _enumerator.UnregisterEndpointNotificationCallback(this);
            }
            catch (COMException)
            {
            }
        }

        List<DeviceHandle> handles;
        lock (_lock)
        {
            handles = new List<DeviceHandle>(_devices.Values);
            _devices.Clear();
        }

        foreach (var handle in handles)
        {
            handle.Dispose();
        }

        if (_enumerator is not null)
        {
            ReleaseIfComObject(_enumerator);
            _enumerator = null;
        }
    }

    // ── Startup-only enumeration (invariant 1) ──────────────────────────────────────────────

    private void EnumerateFlow(EDataFlow nativeFlow)
    {
        _enumerator!.EnumAudioEndpoints(nativeFlow, DEVICE_STATE.DEVICE_STATE_ACTIVE, out IMMDeviceCollection collection);
        try
        {
            collection.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                collection.Item(i, out IMMDevice device);
                try
                {
                    var handle = BuildHandle(device);
                    _devices[handle.Id] = handle;
                }
                catch (COMException)
                {
                    // Device disappeared or refused activation mid-enumeration; drop it and continue.
                    ReleaseIfComObject(device);
                }
            }
        }
        finally
        {
            ReleaseIfComObject(collection);
        }
    }

    private void CacheDefault(AudioFlow flow, AudioRole role, EDataFlow nativeFlow, ERole nativeRole)
    {
        try
        {
            _enumerator!.GetDefaultAudioEndpoint(nativeFlow, nativeRole, out IMMDevice device);
            try
            {
                _defaults[(flow, role)] = GetDeviceId(device);
            }
            finally
            {
                ReleaseIfComObject(device);
            }
        }
        catch (COMException)
        {
            // No default endpoint for this flow/role (e.g. no active devices at all yet).
        }
    }

    /// <summary>Builds one handle: id, friendly name, flow (via IMMEndpoint), IAudioEndpointVolume
    /// (+ its change callback), and - for render devices only - IAudioSessionManager2. Takes
    /// ownership of <paramref name="device"/>: on success it is stored on the returned handle; on
    /// failure the caller is responsible for releasing it (this method does not release partially
    /// activated interfaces if a later step throws - see report).</summary>
    private DeviceHandle BuildHandle(IMMDevice device)
    {
        string id = GetDeviceId(device);
        string friendlyName = GetFriendlyName(device);
        AudioFlow flow = GetFlow(device);

        device.Activate(typeof(IAudioEndpointVolume).GUID, CLSCTX.CLSCTX_ALL, null, out object volumeObj);
        var volume = (IAudioEndpointVolume)volumeObj;

        IAudioSessionManager2? sessionManager = null;
        if (flow == AudioFlow.Render)
        {
            device.Activate(typeof(IAudioSessionManager2).GUID, CLSCTX.CLSCTX_ALL, null, out object sessionManagerObj);
            sessionManager = (IAudioSessionManager2)sessionManagerObj;
        }

        float level = 0f;
        BOOL muted = default;
        try
        {
            volume.GetMasterVolumeLevelScalar(out level);
        }
        catch (COMException)
        {
        }

        try
        {
            volume.GetMute(out muted);
        }
        catch (COMException)
        {
        }

        var handle = new DeviceHandle
        {
            Id = id,
            FriendlyName = friendlyName,
            Flow = flow,
            Device = device,
            Volume = volume,
            SessionManager = sessionManager,
            CachedLevel = level,
            CachedMuted = muted != 0,
        };

        var callback = new EndpointVolumeCallback(handle);
        volume.RegisterControlChangeNotify(callback);
        handle.VolumeCallback = callback;

        return handle;
    }

    private static AudioFlow GetFlow(IMMDevice device)
    {
        var endpoint = (IMMEndpoint)device;
        endpoint.GetDataFlow(out EDataFlow nativeFlow);
        return nativeFlow == EDataFlow.eCapture ? AudioFlow.Capture : AudioFlow.Render;
    }

    private static unsafe string GetDeviceId(IMMDevice device)
    {
        device.GetId(out PWSTR idPtr);
        try
        {
            return idPtr.ToString() ?? string.Empty;
        }
        finally
        {
            if (idPtr.Value != null)
            {
                PInvoke.CoTaskMemFree((void*)idPtr.Value);
            }
        }
    }

    private static string GetFriendlyName(IMMDevice device)
    {
        device.OpenPropertyStore(STGM.STGM_READ, out IPropertyStore store);
        try
        {
            store.GetValue(in PInvoke.PKEY_Device_FriendlyName, out PROPVARIANT value);
            try
            {
                if (value.Anonymous.Anonymous.vt == VARENUM.VT_LPWSTR)
                {
                    return value.Anonymous.Anonymous.Anonymous.pwszVal.ToString() ?? string.Empty;
                }

                return string.Empty;
            }
            finally
            {
                PInvoke.PropVariantClear(ref value);
            }
        }
        finally
        {
            ReleaseIfComObject(store);
        }
    }

    // ── IMMNotificationClient - must not block, must not call Register/Unregister, must hand off
    // fast (spec §2.1, §2.4). Each method here does the minimum COM work needed to describe what
    // changed (a handful of quick calls, no waits, no re-entrant (Un)RegisterEndpointNotification-
    // Callback), takes the lock only to touch the dictionaries, then calls _onChanged so Task 13 can
    // post the result to its queue. See the report for why this differs from a maximally literal
    // reading of "never touch the registry dictionaries inline". ──────────────────────────────────

    void IMMNotificationClient.OnDeviceStateChanged(PCWSTR pwstrDeviceId, DEVICE_STATE dwNewState)
    {
        string id = pwstrDeviceId.ToString() ?? string.Empty;
        if (dwNewState == DEVICE_STATE.DEVICE_STATE_ACTIVE)
        {
            HandleDeviceActivated(id);
        }
        else
        {
            HandleDeviceRemovedOrInactive(id);
        }
    }

    void IMMNotificationClient.OnDeviceAdded(PCWSTR pwstrDeviceId)
    {
        HandleDeviceActivated(pwstrDeviceId.ToString() ?? string.Empty);
    }

    void IMMNotificationClient.OnDeviceRemoved(PCWSTR pwstrDeviceId)
    {
        HandleDeviceRemovedOrInactive(pwstrDeviceId.ToString() ?? string.Empty);
    }

    unsafe void IMMNotificationClient.OnDefaultDeviceChanged(EDataFlow flow, ERole role, PCWSTR pwstrDefaultDeviceId)
    {
        string? id = pwstrDefaultDeviceId.Value == null ? null : pwstrDefaultDeviceId.ToString();
        AudioFlow audioFlow = flow == EDataFlow.eCapture ? AudioFlow.Capture : AudioFlow.Render;
        AudioRole audioRole = ToAudioRole(role);

        AudioDevice? changed = null;
        lock (_lock)
        {
            _defaults[(audioFlow, audioRole)] = id ?? string.Empty;
            if (id is not null && _devices.TryGetValue(id, out var handle))
            {
                changed = ToAudioDeviceLocked(handle);
            }
        }

        // DevicesChangedEventArgs.Device is non-nullable; if the new default isn't (yet) a device
        // we're tracking, or there is no default any more, there is nothing to report.
        if (changed is not null)
        {
            _onChanged(new DevicesChangedEventArgs(DeviceChangeKind.DefaultChanged, changed));
        }
    }

    void IMMNotificationClient.OnPropertyValueChanged(PCWSTR pwstrDeviceId, PROPERTYKEY key)
    {
        // Not required by any binding requirement for this task. A friendly-name change would
        // need re-reading the property store and raising StateChanged; left for a follow-up
        // rather than done partially/incorrectly here.
    }

    private void HandleDeviceActivated(string id)
    {
        if (string.IsNullOrEmpty(id) || _enumerator is null)
        {
            return;
        }

        lock (_lock)
        {
            if (_devices.ContainsKey(id))
            {
                return;
            }
        }

        DeviceHandle handle;
        try
        {
            _enumerator.GetDevice(id, out IMMDevice device);
            device.GetState(out DEVICE_STATE state);
            if (state != DEVICE_STATE.DEVICE_STATE_ACTIVE)
            {
                ReleaseIfComObject(device);
                return;
            }

            handle = BuildHandle(device);
        }
        catch (COMException)
        {
            // Device vanished, or activation failed, between the notification and this lookup.
            return;
        }

        AudioDevice added;
        lock (_lock)
        {
            if (_devices.ContainsKey(handle.Id))
            {
                // Lost a race with another notification for the same device; drop the duplicate.
                handle.Dispose();
                return;
            }

            _devices[handle.Id] = handle;
            added = ToAudioDeviceLocked(handle);
        }

        _onChanged(new DevicesChangedEventArgs(DeviceChangeKind.Added, added));
    }

    private void HandleDeviceRemovedOrInactive(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        DeviceHandle? removed;
        lock (_lock)
        {
            if (!_devices.Remove(id, out removed))
            {
                return;
            }
        }

        AudioDevice device;
        lock (_lock)
        {
            device = ToAudioDeviceLocked(removed);
        }

        removed.Dispose();
        _onChanged(new DevicesChangedEventArgs(DeviceChangeKind.Removed, device));
    }

    // ── Helpers (caller must hold _lock) ────────────────────────────────────────────────────

    private bool TryGetDefaultLocked(AudioFlow flow, AudioRole role, out AudioDevice device)
    {
        if (_defaults.TryGetValue((flow, role), out var id)
            && !string.IsNullOrEmpty(id)
            && _devices.TryGetValue(id, out var handle))
        {
            device = ToAudioDeviceLocked(handle);
            return true;
        }

        device = null!;
        return false;
    }

    private AudioDevice ToAudioDeviceLocked(DeviceHandle handle)
    {
        bool isConsole = _defaults.TryGetValue((handle.Flow, AudioRole.Console), out var c) && c == handle.Id;
        bool isMultimedia = _defaults.TryGetValue((handle.Flow, AudioRole.Multimedia), out var m) && m == handle.Id;
        bool isCommunications = _defaults.TryGetValue((handle.Flow, AudioRole.Communications), out var cm) && cm == handle.Id;
        return new AudioDevice(handle.Id, handle.FriendlyName, handle.Flow, isConsole, isMultimedia, isCommunications);
    }

    private static AudioRole ToAudioRole(ERole role) => role switch
    {
        ERole.eConsole => AudioRole.Console,
        ERole.eCommunications => AudioRole.Communications,
        _ => AudioRole.Multimedia,
    };

    private static void ReleaseIfComObject(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }
}
