using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.Media.Audio.Endpoints;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Shell.PropertiesSystem;
using Volco.Core.Audio;

namespace Volco.Windows.Audio;

/// <summary>
/// Owns every live <see cref="IMMDevice"/> and its <see cref="IAudioEndpointVolume"/> /
/// <see cref="IAudioSessionManager2"/>. Built once at <see cref="Start"/>, kept current by
/// <see cref="IMMNotificationClient"/> notifications. Nothing outside this class calls
/// EnumerateAudioEndpoints/GetDefaultAudioEndpoint - that happens only here, at startup and
/// in response to a live notification (spec §2.3 invariant 1).
///
/// All public members must be called on the AudioEngine's audio thread (MTA), except that
/// <see cref="GetDevices"/>/<see cref="GetDefault"/>/<see cref="Resolve"/> may also be called from
/// other threads (e.g. the UI) - their reads are synchronised against the audio thread's writes.
/// This class does not create or own a thread; see the contracts documented on <see cref="Start"/>
/// and <see cref="DrainPendingChanges"/>.
///
/// IMMNotificationClient and IAudioEndpointVolumeCallback callbacks arrive on an arbitrary
/// MTA/RPC worker thread Windows Audio chooses, never necessarily the audio thread. Per the
/// documented Core Audio contract they must not block and must never wait on a synchronization
/// object (a deadlock risk: the audio thread could be holding that object while doing COM work
/// the RPC callback is itself blocking on). So every callback here does zero COM work and takes
/// no lock - it packages the change into a <see cref="PendingChange"/>, enqueues it, and calls
/// the <c>onPendingWork</c> callback given to the constructor. All the real work (activating COM
/// objects, disposing handles, updating defaults, raising <c>onChanged</c>/<c>onVolumeChanged</c>)
/// happens later, on the audio thread, when it calls <see cref="DrainPendingChanges"/>.
/// </summary>
[ComVisible(true)]
internal sealed class DeviceRegistry : IDisposable, IMMNotificationClient
{
    /// <summary>One cached endpoint and the COM objects hanging off it. Created once, disposed once.</summary>
    internal sealed class DeviceHandle : IDisposable
    {
        public required string Id { get; init; }              // IMMDevice::GetId
        /// <summary>PKEY_Device_FriendlyName; replaced when the user renames the device in Windows.</summary>
        public required string FriendlyName { get; set; }
        public DeviceFormFactor FormFactor { get; init; }     // PKEY_AudioEndpoint_FormFactor
        public required AudioFlow Flow { get; init; }
        public required IMMDevice Device { get; init; }

        /// <summary>Created with the handle; its change callback keeps <see cref="CachedLevel"/>/<see cref="CachedMuted"/> fresh.</summary>
        public required IAudioEndpointVolume Volume { get; init; }

        /// <summary>Render devices only; null for capture endpoints.</summary>
        public IAudioSessionManager2? SessionManager { get; init; }

        /// <summary>Last known state, so TryGetDeviceState never blocks the audio thread on a COM call.</summary>
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
            catch (Exception ex) when (ex is COMException or InvalidComObjectException)
            {
                // Device is likely already gone (or, if a caller disposed this handle out of
                // order relative to SessionRegistry's own teardown, the RCW may already be
                // separated - InvalidComObjectException, not COMException). Either way there is
                // nothing to unregister from; degrade instead of throwing out of Dispose().
            }

            ReleaseIfComObject(SessionManager);
            ReleaseIfComObject(Volume);
            ReleaseIfComObject(Device);
        }
    }

    /// <summary>
    /// Per-device sink for <see cref="IAudioEndpointVolume.RegisterControlChangeNotify"/>. Per the
    /// documented Core Audio contract (spec §2.1/§2.4) this must not block, must not call back into
    /// COM, and must not register/unregister notifications. It does exactly one thing: read the
    /// fields it needs off the notification struct (valid only for the duration of this call),
    /// write the two cached ones onto the handle, and enqueue a <see cref="PendingChange"/> so
    /// <see cref="DrainPendingChanges"/> can raise <c>onVolumeChanged</c> later, on the audio
    /// thread (spec §3 item 3 - this is what makes an externally-originated volume/mute change,
    /// e.g. from the Windows volume mixer, observable at all).
    /// </summary>
    [ComVisible(true)]
    private sealed unsafe class EndpointVolumeCallback : IAudioEndpointVolumeCallback
    {
        private readonly DeviceRegistry _owner;
        private readonly DeviceHandle _handle;

        public EndpointVolumeCallback(DeviceRegistry owner, DeviceHandle handle)
        {
            _owner = owner;
            _handle = handle;
        }

        public void OnNotify(AUDIO_VOLUME_NOTIFICATION_DATA* pNotify)
        {
            if (pNotify is null)
            {
                return;
            }

            float level = pNotify->fMasterVolume;
            bool muted = pNotify->bMuted != 0;
            Guid eventContext = pNotify->guidEventContext;

            _handle.CachedLevel = level;
            _handle.CachedMuted = muted;

            // Enqueue + onPendingWork only - no COM, no lock, no dictionary access, same
            // contract as every other callback in this file (see the class doc comment).
            _owner.Enqueue(PendingChange.VolumeChanged(_handle.Id, level, muted, eventContext));
        }
    }

    private enum PendingChangeKind
    {
        DeviceActivated,
        DeviceRemovedOrInactive,
        DefaultChanged,
        VolumeChanged,
        NameChanged,
    }

    /// <summary>
    /// What an IMMNotificationClient/IAudioEndpointVolumeCallback callback queues for
    /// <see cref="DrainPendingChanges"/> to apply later, on the audio thread. Deliberately tiny and
    /// allocation-free to construct so the callback that builds one never has to do real work.
    /// <see cref="Level"/>/<see cref="Muted"/>/<see cref="EventContext"/> are used only by
    /// <see cref="PendingChangeKind.VolumeChanged"/>; every other kind leaves them at their
    /// default.
    /// </summary>
    private readonly record struct PendingChange(
        PendingChangeKind Kind,
        string DeviceId,
        AudioFlow Flow,
        AudioRole Role,
        float Level,
        bool Muted,
        Guid EventContext)
    {
        public static PendingChange Activated(string id) => new(PendingChangeKind.DeviceActivated, id, default, default, default, default, default);

        public static PendingChange RemovedOrInactive(string id) => new(PendingChangeKind.DeviceRemovedOrInactive, id, default, default, default, default, default);

        public static PendingChange DefaultChangedTo(AudioFlow flow, AudioRole role, string id) => new(PendingChangeKind.DefaultChanged, id, flow, role, default, default, default);

        public static PendingChange NameChanged(string id) => new(PendingChangeKind.NameChanged, id, default, default, default, default, default);

        public static PendingChange VolumeChanged(string id, float level, bool muted, Guid eventContext) =>
            new(PendingChangeKind.VolumeChanged, id, default, default, level, muted, eventContext);
    }

    private readonly Action<DevicesChangedEventArgs> _onChanged;

    /// <summary>
    /// Invoked (via <see cref="DrainPendingChanges"/>, on the audio thread) for a
    /// <see cref="PendingChangeKind.VolumeChanged"/> notification - i.e. an
    /// <see cref="IAudioEndpointVolume"/> change for a device this registry tracks, whether it
    /// originated from this process's own write or externally (the Windows volume mixer, another
    /// app). <see cref="DeviceVolumeChangedEventArgs.IsOwnChange"/> is computed here by comparing
    /// the notification's event-context GUID against <see cref="_engineEventContext"/> (spec §2.3
    /// invariant 6) - the caller passes that value in at construction so this class never needs to
    /// reference <see cref="SessionRegistry"/> directly.
    /// </summary>
    private readonly Action<DeviceVolumeChangedEventArgs> _onVolumeChanged;

    /// <summary>
    /// The process-wide event-context GUID (see <see cref="_onVolumeChanged"/>) - the same value
    /// as <c>SessionRegistry.EngineEventContext</c>, passed in by <c>AudioEngine</c> rather than
    /// referenced by type, so this class stays independent of <c>SessionRegistry</c>.
    /// </summary>
    private readonly Guid _engineEventContext;

    /// <summary>
    /// Invoked by an IMMNotificationClient/IAudioEndpointVolumeCallback callback every time it
    /// enqueues a <see cref="PendingChange"/>. May be called from an arbitrary RPC thread, so -
    /// like the callbacks themselves - it must be non-blocking. AudioEngine wires this to a queue post
    /// that wakes the audio thread; it must never itself do COM work or take a lock.
    /// </summary>
    private readonly Action _onPendingWork;

    private readonly ConcurrentQueue<PendingChange> _pending = new();

    // Protects both dictionaries against the one remaining real race: GetDevices/GetDefault/
    // Resolve may be called from a thread other than the audio thread (e.g. the UI), while the
    // audio thread mutates the dictionaries inside DrainPendingChanges. IMMNotificationClient
    // callbacks never take this lock any more - see the class doc comment - so it is never held
    // by, nor contended against, an RPC callback thread.
    private readonly object _lock = new();
    private readonly Dictionary<string, DeviceHandle> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<(AudioFlow Flow, AudioRole Role), string> _defaults = new();

    private IMMDeviceEnumerator? _enumerator;
    private bool _started;
    private bool _disposed;

    public DeviceRegistry(
        Action<DevicesChangedEventArgs> onChanged,
        Action<DeviceVolumeChangedEventArgs> onVolumeChanged,
        Action onPendingWork,
        Guid engineEventContext)
    {
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        _onVolumeChanged = onVolumeChanged ?? throw new ArgumentNullException(nameof(onVolumeChanged));
        _onPendingWork = onPendingWork ?? throw new ArgumentNullException(nameof(onPendingWork));
        _engineEventContext = engineEventContext;
    }

    /// <summary>
    /// Enumerates every DEVICE_STATE_ACTIVE endpoint (both flows) exactly once, builds a
    /// <see cref="DeviceHandle"/> for each, resolves the three default roles for each flow, and
    /// only then registers this instance as the <see cref="IMMNotificationClient"/>.
    ///
    /// Threading contract: this method must run on an MTA thread (CoInitializeEx(COINIT_MULTITHREADED)
    /// already called on it). Registering IMMNotificationClient from an STA thread is a documented
    /// deadlock source (spec §2.1). DeviceRegistry does not create or own that thread -
    /// AudioEngine does, and it must call Start() from its dedicated audio thread before any other
    /// member of this class is used, and must call <see cref="DrainPendingChanges"/> from that same
    /// thread thereafter (e.g. once per work-queue iteration) to apply queued notifications.
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

    /// <summary>
    /// Applies every <see cref="PendingChange"/> an IMMNotificationClient callback has queued since
    /// the last call: activates COM objects and builds a handle for a newly active device, disposes
    /// and drops a removed/now-inactive one, updates the cached default, and raises
    /// <c>onChanged</c> once per applied change. Safe to do all of that here because, unlike the
    /// callbacks that queued the work, this method is only ever called from the audio thread.
    ///
    /// Threading contract: must be called on the same MTA audio thread as <see cref="Start"/>,
    /// never from the RPC thread a raw notification happened to arrive on.
    /// </summary>
    public void DrainPendingChanges()
    {
        while (_pending.TryDequeue(out var change))
        {
            switch (change.Kind)
            {
                case PendingChangeKind.DeviceActivated:
                    HandleDeviceActivated(change.DeviceId);
                    break;
                case PendingChangeKind.DeviceRemovedOrInactive:
                    HandleDeviceRemovedOrInactive(change.DeviceId);
                    break;
                case PendingChangeKind.DefaultChanged:
                    ApplyDefaultChanged(change.Flow, change.Role, change.DeviceId);
                    break;
                case PendingChangeKind.VolumeChanged:
                    ApplyVolumeChanged(change.DeviceId, change.Level, change.Muted, change.EventContext);
                    break;
                case PendingChangeKind.NameChanged:
                    ApplyNameChanged(change.DeviceId);
                    break;
            }
        }
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
    /// Resolution order: exact id match, then case-insensitive friendly-name match (against the
    /// same payload string). Only a <see cref="DeviceRefKind.Default"/> reference (whose payload
    /// is always "") falls through to the default for the ref's flow/role: an explicit
    /// <see cref="DeviceRefKind.ById"/>/<see cref="DeviceRefKind.ByFriendlyName"/> reference that
    /// does not resolve returns null instead (review item 2) - doing nothing to an action's
    /// configured device is safer than silently retargeting a different one (e.g. an unplugged
    /// headset's mute button must not fall back to muting the default output device).
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
                    if (string.Equals(handle.FriendlyName, reference.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        return handle;
                    }
                }
            }

            if (reference.Kind != DeviceRefKind.Default)
            {
                return null;
            }

            // A DeviceRefKind.Default reference no longer carries a role (spec §4.4) - it maps
            // its flow onto the tracked role the same way AudioEngine.GetDefaultDevice and
            // ToAudioDeviceLocked do, so "the default" means the same device everywhere.
            if (_defaults.TryGetValue((reference.Flow, AudioRoleMapping.ForFlow(reference.Flow)), out var defaultId)
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

                    // Locked (review item 4): GetDevices/GetDefault/Resolve are documented as
                    // callable from other threads (e.g. the UI, while AudioEngine.Start() is
                    // still inside its 10s wait), and used to write here without the lock they
                    // take to read.
                    lock (_lock)
                    {
                        _devices[handle.Id] = handle;
                    }
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException)
                {
                    // Device disappeared, refused activation, or refused QI mid-enumeration;
                    // drop it and continue. BuildHandle releases whatever it activated itself
                    // before rethrowing - device is still ours to release (review item 6).
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
                string id = GetDeviceId(device);

                // Locked - see the matching note in EnumerateFlow (review item 4).
                lock (_lock)
                {
                    _defaults[(flow, role)] = id;
                }
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
    /// failure the caller is responsible for releasing it. Any interface this method itself
    /// activated (<see cref="IAudioEndpointVolume"/>, <see cref="IAudioSessionManager2"/>) before
    /// a later step throws IS rolled back here before rethrowing (review item 6) - a QI can fail
    /// with <see cref="InvalidCastException"/> rather than <see cref="COMException"/>, so both are
    /// caught, matching the precedent in <see cref="SessionRegistry"/>.</summary>
    private DeviceHandle BuildHandle(IMMDevice device)
    {
        string id = GetDeviceId(device);
        string friendlyName = GetFriendlyName(device);
        var formFactor = GetFormFactor(device);
        AudioFlow flow = GetFlow(device);

        IAudioEndpointVolume? volume = null;
        IAudioSessionManager2? sessionManager = null;
        try
        {
            device.Activate(typeof(IAudioEndpointVolume).GUID, CLSCTX.CLSCTX_ALL, null, out object volumeObj);
            volume = (IAudioEndpointVolume)volumeObj;

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
                FormFactor = formFactor,
                Flow = flow,
                Device = device,
                Volume = volume,
                SessionManager = sessionManager,
                CachedLevel = level,
                CachedMuted = muted != 0,
            };

            var callback = new EndpointVolumeCallback(this, handle);
            volume.RegisterControlChangeNotify(callback);
            handle.VolumeCallback = callback;

            return handle;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // Roll back whatever was already activated before this step failed. `device` itself
            // is NOT released here - the caller (EnumerateFlow/HandleDeviceActivated) still owns
            // it and is responsible for releasing it on a thrown exception, per this method's
            // contract.
            ReleaseIfComObject(sessionManager);
            ReleaseIfComObject(volume);
            throw;
        }
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

    private static DeviceFormFactor GetFormFactor(IMMDevice device)
    {
        try
        {
            device.OpenPropertyStore(STGM.STGM_READ, out IPropertyStore store);
            try
            {
                store.GetValue(in PInvoke.PKEY_AudioEndpoint_FormFactor, out PROPVARIANT value);
                try
                {
                    if (value.Anonymous.Anonymous.vt != VARENUM.VT_UI4) return DeviceFormFactor.Unknown;
                    return value.Anonymous.Anonymous.Anonymous.ulVal switch
                    {
                        1 => DeviceFormFactor.Speakers,
                        3 => DeviceFormFactor.Headphones,
                        4 => DeviceFormFactor.Microphone,
                        5 or 6 => DeviceFormFactor.Headset,
                        9 => DeviceFormFactor.Display,
                        _ => DeviceFormFactor.Other,
                    };
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
        catch (COMException)
        {
            return DeviceFormFactor.Unknown;
        }
    }

    // ── IMMNotificationClient - must not block and must never wait on a synchronization object
    // (spec §2.1, §2.4): no COM calls, no lock, no dictionary access, no re-entrant
    // (Un)RegisterEndpointNotificationCallback, no invoking _onChanged directly. Each method here
    // does exactly one thing: build a PendingChange from the (already-marshalled-to-us) arguments,
    // enqueue it, call _onPendingWork, and return. The real work happens later in
    // DrainPendingChanges, on the audio thread. ─────────────────────────────────────────────────

    void IMMNotificationClient.OnDeviceStateChanged(PCWSTR pwstrDeviceId, DEVICE_STATE dwNewState)
    {
        string id = pwstrDeviceId.ToString() ?? string.Empty;
        Enqueue(dwNewState == DEVICE_STATE.DEVICE_STATE_ACTIVE
            ? PendingChange.Activated(id)
            : PendingChange.RemovedOrInactive(id));
    }

    void IMMNotificationClient.OnDeviceAdded(PCWSTR pwstrDeviceId)
    {
        Enqueue(PendingChange.Activated(pwstrDeviceId.ToString() ?? string.Empty));
    }

    void IMMNotificationClient.OnDeviceRemoved(PCWSTR pwstrDeviceId)
    {
        Enqueue(PendingChange.RemovedOrInactive(pwstrDeviceId.ToString() ?? string.Empty));
    }

    unsafe void IMMNotificationClient.OnDefaultDeviceChanged(EDataFlow flow, ERole role, PCWSTR pwstrDefaultDeviceId)
    {
        string id = pwstrDefaultDeviceId.Value == null ? string.Empty : (pwstrDefaultDeviceId.ToString() ?? string.Empty);
        AudioFlow audioFlow = flow == EDataFlow.eCapture ? AudioFlow.Capture : AudioFlow.Render;
        AudioRole audioRole = ToAudioRole(role);
        Enqueue(PendingChange.DefaultChangedTo(audioFlow, audioRole, id));
    }

    void IMMNotificationClient.OnPropertyValueChanged(PCWSTR pwstrDeviceId, PROPERTYKEY key)
    {
        // A rename in Windows (Sound settings) changes the friendly name. Other properties change
        // often and are of no interest. Comparing the key is plain memory, no COM.
        var name = PInvoke.PKEY_Device_FriendlyName;
        if (key.fmtid != name.fmtid || key.pid != name.pid) return;
        Enqueue(PendingChange.NameChanged(pwstrDeviceId.ToString() ?? string.Empty));
    }

    private void Enqueue(PendingChange change)
    {
        _pending.Enqueue(change);
        _onPendingWork();
    }

    // ── Applied only from DrainPendingChanges, i.e. only on the audio thread ───────────────────

    /// <summary>
    /// Raises <c>onVolumeChanged</c> for a drained <see cref="PendingChangeKind.VolumeChanged"/>
    /// notification (spec §3 item 3). No dictionary/lock work is needed here beyond what
    /// <see cref="EndpointVolumeCallback"/> already did (updating <c>CachedLevel</c>/
    /// <c>CachedMuted</c> directly on the handle at notification time) - this method's only job is
    /// computing <see cref="DeviceVolumeChangedEventArgs.IsOwnChange"/> and forwarding.
    /// </summary>
    private void ApplyVolumeChanged(string deviceId, float level, bool muted, Guid eventContext)
    {
        bool isOwnChange = eventContext == _engineEventContext;
        _onVolumeChanged(new DeviceVolumeChangedEventArgs(deviceId, level, muted, isOwnChange));
    }

    /// <summary>A device was renamed in Windows: re-reads its friendly name and raises
    /// <see cref="DeviceChangeKind.StateChanged"/>, so the pickers, the labels and the names stored
    /// in the config follow.</summary>
    private void ApplyNameChanged(string id)
    {
        DeviceHandle? handle;
        lock (_lock)
        {
            if (!_devices.TryGetValue(id, out handle)) return;
        }

        string name;
        try
        {
            name = GetFriendlyName(handle.Device);
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            return;   // unplugged meanwhile: its removal is queued too
        }

        AudioDevice device;
        lock (_lock)
        {
            if (name.Length == 0 || name == handle.FriendlyName || !_devices.ContainsKey(id)) return;
            handle.FriendlyName = name;
            device = ToAudioDeviceLocked(handle);
        }

        _onChanged(new DevicesChangedEventArgs(DeviceChangeKind.StateChanged, device));
    }

    private void ApplyDefaultChanged(AudioFlow flow, AudioRole role, string id)
    {
        AudioDevice? changed = null;
        lock (_lock)
        {
            _defaults[(flow, role)] = id;
            if (!string.IsNullOrEmpty(id) && _devices.TryGetValue(id, out var handle))
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

        IMMDevice? device = null;
        DeviceHandle handle;
        try
        {
            _enumerator.GetDevice(id, out device);
            device.GetState(out DEVICE_STATE state);
            if (state != DEVICE_STATE.DEVICE_STATE_ACTIVE)
            {
                ReleaseIfComObject(device);
                return;
            }

            handle = BuildHandle(device);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // Device vanished, activation failed, or refused QI between the notification and
            // this lookup. BuildHandle rolls back what it activated itself on failure, but
            // `device` is still ours to release here (review item 6 - this previously leaked it
            // whenever GetState/BuildHandle threw after a successful GetDevice).
            ReleaseIfComObject(device);
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

        // Raise before disposing (review item 1): _onChanged drives AudioEngine ->
        // SessionRegistry.Detach for this device id, which calls
        // UnregisterSessionNotification on `removed.SessionManager` - the SAME COM object
        // instance as SessionRegistry's AttachedDevice.Manager (DeviceRegistry.DeviceHandle
        // owns it; SessionRegistry never AddRef's its own copy). Disposing `removed` first would
        // release that RCW out from under Detach, which throws InvalidComObjectException (not
        // COMException) and aborts before SessionRegistry's own per-session cleanup runs -
        // leaking the session notification registration and every session handle on this
        // device, forever, on every unplug. This mirrors the ordering AudioEngine.Dispose()
        // already uses at shutdown: dispose SessionRegistry before DeviceRegistry. `_onChanged`
        // is invoked outside both lock blocks above, so `_lock` is never held across this call.
        _onChanged(new DevicesChangedEventArgs(DeviceChangeKind.Removed, device));
        removed.Dispose();
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

    /// <summary>
    /// <see cref="AudioDevice.IsDefault"/> means "is the cached default for this device's flow's
    /// mapped role" (spec §4.4: Render → Multimedia, Capture → Communications) - the same mapping
    /// <see cref="Resolve"/> uses for a <see cref="DeviceRefKind.Default"/> reference and
    /// <see cref="AudioEngine.GetDefaultDevice"/> uses for its role argument. Console-role state
    /// still lives in <see cref="_defaults"/> (populated by <see cref="CacheDefault"/> and kept
    /// current by <see cref="ApplyDefaultChanged"/>, since <c>SetDefaultDevice</c> still writes
    /// all three roles) but no longer surfaces on <see cref="AudioDevice"/> - it has no consumer
    /// outside this class.
    /// </summary>
    private AudioDevice ToAudioDeviceLocked(DeviceHandle handle)
    {
        bool isDefault = _defaults.TryGetValue((handle.Flow, AudioRoleMapping.ForFlow(handle.Flow)), out var id) && id == handle.Id;
        return new AudioDevice(handle.Id, handle.FriendlyName, handle.Flow, isDefault, handle.FormFactor);
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
