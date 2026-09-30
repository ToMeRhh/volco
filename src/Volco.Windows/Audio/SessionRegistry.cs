using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Threading;
using Volco.Core.Audio;

namespace Volco.Windows.Audio;

/// <summary>
/// Owns every live audio session across ALL active render devices (not just the default one),
/// keyed by <see cref="IAudioSessionControl2.GetSessionInstanceIdentifier"/> so a slider write is
/// a dictionary lookup plus one COM call — never an enumeration. This is the fix for the core
/// performance bug: the old code re-enumerated every session, on a freshly-activated COM object,
/// up to 50 times per second (spec §1).
///
/// All public members must be called on the AudioEngine's dedicated audio thread (MTA) - the same
/// thread that calls <see cref="DeviceRegistry.Start"/> and drives
/// <see cref="DeviceRegistry.DrainPendingChanges"/>. This class does not create or own that
/// thread.
///
/// Threading contract mirrors <see cref="DeviceRegistry"/> exactly: <see cref="IAudioSessionNotification"/>
/// and <see cref="IAudioSessionEvents"/> callbacks arrive on an arbitrary MTA/RPC worker thread
/// Windows Audio chooses. Per the documented Core Audio contract they must not block, must never
/// wait on a synchronization object, and must not register/unregister notifications from inside a
/// callback. So every callback here does zero COM work and takes no lock - it packages the change
/// into a <see cref="PendingChange"/>, enqueues it, and calls the <c>onPendingWork</c> callback
/// given to the constructor (itself non-blocking - it must tolerate being invoked from that same
/// arbitrary RPC thread). All the real work (QueryInterface, resolving the image name, activating
/// <see cref="ISimpleAudioVolume"/>, updating the dictionary, raising <c>onChanged</c>) happens
/// later, on the audio thread, when it calls <see cref="DrainPendingChanges"/>.
/// </summary>
internal sealed class SessionRegistry : IDisposable
{
    /// <summary>
    /// Process-wide event-context GUID passed to every session volume write via
    /// <see cref="ISimpleAudioVolume.SetMasterVolume"/> (spec §2.3 invariant 6, task requirement
    /// 9). Task 13's <c>AudioEngine</c> should reuse this exact value for
    /// <c>IAudioEndpointVolume::SetMasterVolumeLevelScalar</c> device-level writes too, and compare
    /// an incoming callback's own EventContext against it to distinguish "we caused this change"
    /// from "the user changed it elsewhere" (avoiding OSD/controller-LED feedback loops).
    /// </summary>
    internal static readonly Guid EngineEventContext = Guid.NewGuid();

    /// <summary>One cached session and the COM objects hanging off it. Created once (on
    /// creation/enumeration), disposed once (on expiry/disconnect/detach).</summary>
    private sealed class SessionHandle : IDisposable
    {
        public required string InstanceId { get; init; }      // IAudioSessionControl2::GetSessionInstanceIdentifier
        public required uint ProcessId { get; init; }
        public required string ImageName { get; init; }        // resolved once, at creation
        public required string DeviceId { get; init; }
        public required bool IsSystemSounds { get; init; }

        /// <summary>The long-lived owned reference. <see cref="Volume"/> is a QueryInterface view
        /// of this same underlying COM object (same pattern as <c>(IMMEndpoint)device</c> in
        /// <see cref="DeviceRegistry.GetFlow"/>) - only <see cref="Control"/> is released.</summary>
        public required IAudioSessionControl2 Control { get; init; }

        public required ISimpleAudioVolume Volume { get; init; }

        /// <summary>Set once <see cref="IAudioSessionControl.RegisterAudioSessionNotification"/>
        /// succeeds, so <see cref="Dispose"/> can unregister it.</summary>
        public IAudioSessionEvents? EventsSink { get; set; }

        public AudioSessionInfo ToInfo() => new(InstanceId, ProcessId, ImageName, DeviceId, IsSystemSounds);

        public void Dispose()
        {
            try
            {
                if (EventsSink is not null)
                {
                    Control.UnregisterAudioSessionNotification(EventsSink);
                }
            }
            catch (COMException)
            {
                // Session is likely already gone; nothing to unregister from.
            }

            // Volume is a QI view of Control's COM identity, not a separately activated object -
            // releasing Control alone is correct (mirrors DeviceRegistry's IMMEndpoint precedent).
            ReleaseIfComObject(Control);
        }
    }

    /// <summary>One attached render device: what's needed to unregister
    /// <see cref="IAudioSessionNotification"/> on <see cref="Detach"/>. Does not own
    /// <see cref="Manager"/> - <see cref="DeviceRegistry.DeviceHandle"/> does, and releases it in
    /// its own <c>Dispose</c>.</summary>
    private sealed class AttachedDevice
    {
        public required IAudioSessionManager2 Manager { get; init; }
        public required IAudioSessionNotification Notification { get; init; }

        /// <summary>True only once <see cref="IAudioSessionManager2.RegisterSessionNotification"/>
        /// has actually succeeded for <see cref="Notification"/>. <see cref="Attach"/> constructs
        /// this object and stores it before registering (see its ordering note), so there is a
        /// window where a rollback could otherwise try to unregister a sink Core Audio never had on
        /// file; gating on this flag avoids calling that API in an invalid state.</summary>
        public bool Registered { get; set; }
    }

    /// <summary>
    /// Sink for <see cref="IAudioSessionManager2.RegisterSessionNotification"/>. Per the documented
    /// Core Audio contract (spec §2.1/§2.4) this must not block, must not call back into COM, and
    /// must not register/unregister notifications. It does exactly one thing: package the raw
    /// (already-AddRef'd, not-yet-inspected) session pointer and the owning device id into a
    /// <see cref="PendingChange"/>, enqueue it, and call <c>onPendingWork</c>.
    /// </summary>
    [ComVisible(true)]
    private sealed class SessionNotificationSink : IAudioSessionNotification
    {
        private readonly SessionRegistry _owner;
        private readonly string _deviceId;

        public SessionNotificationSink(SessionRegistry owner, string deviceId)
        {
            _owner = owner;
            _deviceId = deviceId;
        }

        public void OnSessionCreated(IAudioSessionControl NewSession)
        {
            _owner.Enqueue(PendingChange.Created(_deviceId, NewSession));
        }
    }

    /// <summary>
    /// Per-session sink for <see cref="IAudioSessionControl.RegisterAudioSessionNotification"/>.
    /// Same non-blocking, no-COM, no-lock contract as <see cref="SessionNotificationSink"/>. Only
    /// <see cref="OnStateChanged"/> (Expired) and <see cref="OnSessionDisconnected"/> matter here
    /// (spec §2.3 invariant 3); the rest are no-ops required only to satisfy the interface.
    /// </summary>
    [ComVisible(true)]
    private sealed unsafe class SessionEventsSink : IAudioSessionEvents
    {
        private readonly SessionRegistry _owner;
        private readonly string _instanceId;

        public SessionEventsSink(SessionRegistry owner, string instanceId)
        {
            _owner = owner;
            _instanceId = instanceId;
        }

        public void OnDisplayNameChanged(PCWSTR NewDisplayName, Guid* EventContext)
        {
        }

        public void OnIconPathChanged(PCWSTR NewIconPath, Guid* EventContext)
        {
        }

        public void OnSimpleVolumeChanged(float NewVolume, BOOL NewMute, Guid* EventContext)
        {
        }

        public void OnChannelVolumeChanged(uint ChannelCount, float[] NewChannelVolumeArray, uint ChangedChannel, Guid* EventContext)
        {
        }

        public void OnGroupingParamChanged(Guid* NewGroupingParam, Guid* EventContext)
        {
        }

        public void OnStateChanged(AudioSessionState NewState)
        {
            if (NewState == AudioSessionState.AudioSessionStateExpired)
            {
                _owner.Enqueue(PendingChange.Expired(_instanceId));
            }
        }

        public void OnSessionDisconnected(AudioSessionDisconnectReason DisconnectReason)
        {
            _owner.Enqueue(PendingChange.Disconnected(_instanceId));
        }
    }

    private enum PendingKind
    {
        SessionCreated,
        SessionExpired,
        SessionDisconnected,
    }

    /// <summary>
    /// What a callback queues for <see cref="DrainPendingChanges"/> to apply later, on the audio
    /// thread. For <see cref="PendingKind.SessionCreated"/>, <see cref="NewSession"/> carries the
    /// raw, already-AddRef'd interface pointer straight from the callback - inspecting it
    /// (QueryInterface, GetSessionInstanceIdentifier, ...) is deferred work, not a callback-time
    /// COM call.
    /// </summary>
    private readonly record struct PendingChange(PendingKind Kind, string DeviceId, string InstanceId, IAudioSessionControl? NewSession)
    {
        public static PendingChange Created(string deviceId, IAudioSessionControl session) => new(PendingKind.SessionCreated, deviceId, "", session);

        public static PendingChange Expired(string instanceId) => new(PendingKind.SessionExpired, "", instanceId, null);

        public static PendingChange Disconnected(string instanceId) => new(PendingKind.SessionDisconnected, "", instanceId, null);
    }

    private readonly Action<SessionsChangedEventArgs> _onChanged;

    /// <summary>Invoked by a callback every time it enqueues a <see cref="PendingChange"/>. May be
    /// called from an arbitrary RPC thread - like the callbacks themselves, it must be
    /// non-blocking and must not touch COM or take a lock. Task 13 wires this to a queue post that
    /// wakes the audio thread.</summary>
    private readonly Action _onPendingWork;

    private readonly ConcurrentQueue<PendingChange> _pending = new();

    // Protects both dictionaries. Unlike DeviceRegistry's GetDevices/GetDefault/Resolve, every
    // member of this class is documented as audio-thread-only, so today there is no real cross-
    // thread race - but IAudioEngine.GetSessions() (Task 13's public surface, spec §2.2) is a
    // "served from the in-memory registry" query in the same family as DeviceRegistry.GetDevices,
    // which IS called from other threads. Keeping the same locking discipline here costs nothing
    // uncontended and avoids a second race to rediscover if that changes. Callbacks never take
    // this lock - see the class doc comment - so it is never held by, nor contended against, an
    // RPC callback thread.
    private readonly object _lock = new();
    private readonly Dictionary<string, SessionHandle> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AttachedDevice> _devices = new(StringComparer.Ordinal);

    /// <summary>Reused across <see cref="ApplyVolume"/> calls (review item 10) - this class is
    /// audio-thread-only, so a single scratch buffer is safe without extra locking, and avoids
    /// the per-call <c>Where(...).ToList()</c> allocation on the ~50Hz slider path.</summary>
    private readonly List<SessionHandle> _applyVolumeTargets = new();

    private bool _disposed;

    public SessionRegistry(Action<SessionsChangedEventArgs> onChanged, Action onPendingWork)
    {
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        _onPendingWork = onPendingWork ?? throw new ArgumentNullException(nameof(onPendingWork));
    }

    /// <summary>
    /// Attaches one render device: enumerates its currently-live sessions once, builds a handle
    /// for each, and only then registers this instance for future session notifications on that
    /// device's <see cref="IAudioSessionManager2"/>.
    ///
    /// Requirement 1 (highest-risk line in this class): <see cref="IAudioSessionManager2.GetSessionEnumerator"/>
    /// MUST be called before <see cref="IAudioSessionManager2.RegisterSessionNotification"/>.
    /// Without that prior call, Windows silently registers the notification but never delivers
    /// anything through it - no error, no crash (spec §2.4).
    ///
    /// That ordering interacts with a second one: <see cref="BuildAndStoreSession"/> (used both by
    /// the initial enumeration below and by a drained <c>OnSessionCreated</c>) refuses to build a
    /// session for a device id it does not find in the attached-devices dictionary, so a device
    /// must be recorded there BEFORE its sessions are enumerated - not after, or every pre-existing
    /// session found here is silently dropped. So the actual order is: construct the sink (but do not
    /// register it yet) -&gt; record the device -&gt; enumerate and build its existing sessions -&gt;
    /// only then register the sink. This keeps "enumerate before register" true for Core Audio
    /// while keeping "device recorded before its sessions are built" true for
    /// <see cref="BuildAndStoreSession"/>.
    ///
    /// If enumeration or registration throws, the device entry and any sessions already built for
    /// it in this call are rolled back (as an ordinary <see cref="Detach"/>, without raising
    /// events) before the exception propagates, so a failed attach leaves nothing half-registered.
    ///
    /// No-op for a capture device (<see cref="DeviceRegistry.DeviceHandle.SessionManager"/> is
    /// null) and for a device id already attached.
    /// </summary>
    public void Attach(DeviceRegistry.DeviceHandle device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var manager = device.SessionManager;
        if (manager is null)
        {
            return;
        }

        lock (_lock)
        {
            if (_devices.ContainsKey(device.Id))
            {
                return;
            }
        }

        // Sink is constructed but NOT registered yet - see the ordering note above.
        var sink = new SessionNotificationSink(this, device.Id);
        var attachedDevice = new AttachedDevice { Manager = manager, Notification = sink };

        lock (_lock)
        {
            _devices[device.Id] = attachedDevice;
        }

        try
        {
            // Requirement 1 / spec §2.4: enumerate once, unconditionally, BEFORE registering.
            IAudioSessionEnumerator sessionEnumerator = manager.GetSessionEnumerator();
            try
            {
                sessionEnumerator.GetCount(out int count);
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        sessionEnumerator.GetSession(i, out IAudioSessionControl session);
                        BuildAndStoreSession(device.Id, session, raiseChanged: false);
                    }
                    catch (COMException)
                    {
                        // Session vanished mid-enumeration, or refused QI; skip it and continue.
                    }
                }
            }
            finally
            {
                ReleaseIfComObject(sessionEnumerator);
            }

            manager.RegisterSessionNotification(sink);
            attachedDevice.Registered = true;
        }
        catch (COMException)
        {
            // Leave nothing half-registered: drop the device entry and dispose whatever sessions
            // the enumeration above already built for it. raiseChanged: false because, from the
            // caller's perspective, this device was never successfully attached in the first
            // place - there is nothing for a consumer to have observed yet.
            DetachInternal(device.Id, raiseChanged: false);
            throw;
        }
    }

    /// <summary>Unregisters <paramref name="deviceId"/>'s session notification and disposes every
    /// session handle that belongs to it, raising <see cref="SessionChangeKind.Expired"/> for
    /// each. No-op if the device was never attached.</summary>
    public void Detach(string deviceId) => DetachInternal(deviceId, raiseChanged: true);

    public IReadOnlyList<AudioSessionInfo> GetSessions()
    {
        lock (_lock)
        {
            return _sessions.Values.Select(h => h.ToInfo()).ToList();
        }
    }

    /// <summary>
    /// Requirement 5: walks the in-memory dictionary and never enumerates. Writes via
    /// <see cref="ISimpleAudioVolume.SetMasterVolume"/>, passing <see cref="EngineEventContext"/>.
    /// <see cref="SessionFilter.ByImageNames"/> matches case-insensitively;
    /// <see cref="SessionFilter.Unmapped"/> matches every live session whose image name is NOT in
    /// <c>MappedNames</c>, also case-insensitively.
    /// </summary>
    public void ApplyVolume(SessionFilter filter, float level)
    {
        // Defense in depth: the IAudioEngine contract documents level as [0,1] and the old
        // AudioManager clamped - do not rely on every caller already having done so.
        level = Math.Clamp(level, 0f, 1f);

        // Manual loops + a scratch list/set instead of the LINQ this replaced (review item 10):
        // at ~50Hz with ~20 live sessions, `Where(...).ToList()` plus a per-session
        // `names.Any(n => ...)` closure was allocating a closure, an iterator, and a delegate
        // PER SESSION PER TICK - ~2,000 delegate allocations/second of gen0 garbage on the one
        // path this project exists to make cheap. The HashSet below is built once per call, not
        // once per session.
        var targets = _applyVolumeTargets;
        targets.Clear();

        lock (_lock)
        {
            switch (filter)
            {
                case SessionFilter.ByImageNames byNames:
                    var claimed = ToOrdinalIgnoreCaseSet(byNames.Names);
                    foreach (var handle in _sessions.Values)
                    {
                        if (claimed.Contains(handle.ImageName))
                        {
                            targets.Add(handle);
                        }
                    }
                    break;

                case SessionFilter.Unmapped unmapped:
                    var mapped = ToOrdinalIgnoreCaseSet(unmapped.MappedNames);
                    foreach (var handle in _sessions.Values)
                    {
                        if (!mapped.Contains(handle.ImageName))
                        {
                            targets.Add(handle);
                        }
                    }
                    break;
            }
        }

        foreach (var handle in targets)
        {
            try
            {
                WriteVolume(handle.Volume, level);
            }
            catch (COMException)
            {
                // Session likely vanished between the snapshot above and this write; the
                // corresponding OnStateChanged/OnSessionDisconnected notification (if any) will
                // clean it up on a later DrainPendingChanges. Not fatal to this call.
            }
        }

        targets.Clear();
    }

    /// <summary>Applies every <see cref="PendingChange"/> a callback has queued since the last
    /// call. Must be called only on the audio thread - never from the RPC thread a raw
    /// notification happened to arrive on. Tolerates a session that has already vanished (e.g. two
    /// callbacks raced, or the owning device was detached first).</summary>
    public void DrainPendingChanges()
    {
        while (_pending.TryDequeue(out var change))
        {
            switch (change.Kind)
            {
                case PendingKind.SessionCreated:
                    BuildAndStoreSession(change.DeviceId, change.NewSession!, raiseChanged: true);
                    break;
                case PendingKind.SessionExpired:
                case PendingKind.SessionDisconnected:
                    HandleSessionRemoved(change.InstanceId);
                    break;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        List<string> deviceIds;
        lock (_lock)
        {
            deviceIds = new List<string>(_devices.Keys);
        }

        foreach (var id in deviceIds)
        {
            // No onChanged during teardown - mirrors DeviceRegistry.Dispose(), which disposes
            // handles directly without raising events.
            DetachInternal(id, raiseChanged: false);
        }
    }

    // ── Deferred work (runs only from DrainPendingChanges / Attach, i.e. only on the audio thread) ──

    /// <summary>
    /// Turns a raw <see cref="IAudioSessionControl"/> (either from the initial enumeration in
    /// <see cref="Attach"/>, or from a drained <see cref="PendingKind.SessionCreated"/>) into a
    /// tracked <see cref="SessionHandle"/>: QueryInterface to <see cref="IAudioSessionControl2"/>,
    /// key by <see cref="IAudioSessionControl2.GetSessionInstanceIdentifier"/> (requirement 3 -
    /// never <c>GetSessionIdentifier</c>, which is shared across all sessions of one app), resolve
    /// the image name exactly once (requirement 4), activate <see cref="ISimpleAudioVolume"/>, and
    /// register for per-session state events (requirement 2).
    ///
    /// Requirement 8: this never re-enumerates to "confirm" the session is real - the notification
    /// (or the one-time enumeration in <see cref="Attach"/>) is treated as authoritative.
    /// </summary>
    private void BuildAndStoreSession(string deviceId, IAudioSessionControl rawSession, bool raiseChanged)
    {
        lock (_lock)
        {
            if (!_devices.ContainsKey(deviceId))
            {
                // Device was detached before this queued creation was drained; drop it.
                ReleaseIfComObject(rawSession);
                return;
            }
        }

        IAudioSessionControl2 control2;
        try
        {
            control2 = (IAudioSessionControl2)rawSession;
        }
        catch (InvalidCastException)
        {
            ReleaseIfComObject(rawSession);
            return;
        }

        string instanceId;
        try
        {
            instanceId = GetSessionInstanceIdentifier(control2);
        }
        catch (COMException)
        {
            ReleaseIfComObject(control2);
            return;
        }

        if (string.IsNullOrEmpty(instanceId))
        {
            ReleaseIfComObject(control2);
            return;
        }

        lock (_lock)
        {
            if (_sessions.ContainsKey(instanceId))
            {
                // Enumeration and a live notification raced for the same session; drop the
                // duplicate (requirement 8 - the first one in wins, no re-enumeration to compare).
                ReleaseIfComObject(control2);
                return;
            }
        }

        // Requirement 4: resolve IsSystemSoundsSession/pid/image name exactly once, here, never
        // again for this session's lifetime.
        bool isSystemSounds;
        try
        {
            isSystemSounds = IsSystemSoundsSession(control2);
        }
        catch (COMException)
        {
            isSystemSounds = false;
        }

        uint pid = 0;
        string imageName = string.Empty;
        if (!isSystemSounds)
        {
            try
            {
                control2.GetProcessId(out pid);
            }
            catch (COMException)
            {
            }

            imageName = ResolveImageName(pid);
        }

        ISimpleAudioVolume volume;
        try
        {
            volume = (ISimpleAudioVolume)control2;
        }
        catch (InvalidCastException)
        {
            ReleaseIfComObject(control2);
            return;
        }

        var handle = new SessionHandle
        {
            InstanceId = instanceId,
            ProcessId = pid,
            ImageName = imageName,
            DeviceId = deviceId,
            IsSystemSounds = isSystemSounds,
            Control = control2,
            Volume = volume,
        };

        // Requirement 2: one ISimpleAudioVolume per session, released on Expired/Disconnected.
        var eventsSink = new SessionEventsSink(this, instanceId);
        try
        {
            control2.RegisterAudioSessionNotification(eventsSink);
            handle.EventsSink = eventsSink;
        }
        catch (COMException)
        {
            // Degraded but not fatal: this one session won't self-remove on expiry/disconnect
            // until its owning device is detached. Still tracked and controllable until then.
        }

        AudioSessionInfo info;
        lock (_lock)
        {
            if (_sessions.ContainsKey(instanceId))
            {
                handle.Dispose();
                return;
            }

            _sessions[instanceId] = handle;
            info = handle.ToInfo();
        }

        if (raiseChanged)
        {
            _onChanged(new SessionsChangedEventArgs(SessionChangeKind.Created, info));
        }
    }

    private void HandleSessionRemoved(string instanceId)
    {
        SessionHandle? removed;
        lock (_lock)
        {
            _sessions.Remove(instanceId, out removed);
        }

        if (removed is null)
        {
            // Already gone - e.g. the owning device was detached first. Tolerate it.
            return;
        }

        var info = removed.ToInfo();
        removed.Dispose();
        _onChanged(new SessionsChangedEventArgs(SessionChangeKind.Expired, info));
    }

    private void DetachInternal(string deviceId, bool raiseChanged)
    {
        AttachedDevice? attached;
        lock (_lock)
        {
            _devices.Remove(deviceId, out attached);
        }

        if (attached is not null && attached.Registered)
        {
            try
            {
                attached.Manager.UnregisterSessionNotification(attached.Notification);
            }
            catch (Exception ex) when (ex is COMException or InvalidComObjectException)
            {
                // Device is likely already gone. InvalidComObjectException (not COMException)
                // is what a separated RCW throws - e.g. if DeviceRegistry ever disposed the
                // DeviceHandle that owns this same SessionManager instance before raising the
                // DevicesChanged notification that drives this Detach (review item 1 hardening:
                // degrade instead of aborting DetachInternal before the session cleanup below).
            }
        }

        List<SessionHandle> owned;
        lock (_lock)
        {
            owned = _sessions.Values.Where(h => h.DeviceId == deviceId).ToList();
            foreach (var handle in owned)
            {
                _sessions.Remove(handle.InstanceId);
            }
        }

        foreach (var handle in owned)
        {
            var info = handle.ToInfo();
            handle.Dispose();
            if (raiseChanged)
            {
                _onChanged(new SessionsChangedEventArgs(SessionChangeKind.Expired, info));
            }
        }
    }

    private void Enqueue(PendingChange change)
    {
        _pending.Enqueue(change);
        _onPendingWork();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static unsafe string GetSessionInstanceIdentifier(IAudioSessionControl2 control)
    {
        control.GetSessionInstanceIdentifier(out PWSTR idPtr);
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

    /// <summary>
    /// <c>IAudioSessionControl2.IsSystemSoundsSession()</c> is confirmed (build-verified) to return
    /// <see cref="HRESULT"/> directly - PreserveSig, since S_OK/S_FALSE are both meaningful
    /// successes here - rather than being wrapped as a void method that throws only on failure.
    /// </summary>
    private static HRESULT IsSystemSoundsSessionCore(IAudioSessionControl2 control) => control.IsSystemSoundsSession();

    private static bool IsSystemSoundsSession(IAudioSessionControl2 control) => IsSystemSoundsSessionCore(control).Value == 0;

    /// <summary>Requirement 4: GetProcessId → OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) →
    /// QueryFullProcessImageNameW → Path.GetFileName, then close the handle. Never
    /// <c>Process.GetProcessById</c>. Notes on the generated shapes actually used here (see report
    /// for the full per-call type audit and confirmed/unconfirmed breakdown):
    /// <list type="bullet">
    /// <item><c>PInvoke.OpenProcess</c> (the plain name) returns a raw <c>HANDLE</c>. CsWin32 also
    /// generates a separate, differently-named <c>PInvoke.OpenProcess_SafeHandle</c> that returns a
    /// <see cref="SafeHandle"/>-derived type - that is the one called below, disposed via
    /// <c>using</c> instead of a manual <c>CloseHandle</c>.</item>
    /// <item>The native export is <c>QueryFullProcessImageNameW</c> (kept as the
    /// <c>NativeMethods.txt</c> entry name), but CsWin32 strips the trailing "W" from the generated
    /// C# name, and binds to its friendly overload - <c>bool QueryFullProcessImageName(SafeHandle,
    /// PROCESS_NAME_FORMAT, Span&lt;char&gt;, ref uint)</c> - not a raw <c>HANDLE</c>/<c>PWSTR</c>
    /// shape. Its first parameter's declared type is the base <see cref="SafeHandle"/> class, so
    /// whatever concrete type <c>OpenProcess_SafeHandle</c> returns is accepted directly.</item>
    /// </list>
    /// </summary>
    private static string ResolveImageName(uint pid)
    {
        if (pid == 0)
        {
            return string.Empty;
        }

        using var process = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process.IsInvalid)
        {
            return string.Empty;
        }

        try
        {
            Span<char> buffer = stackalloc char[260]; // MAX_PATH
            uint size = (uint)buffer.Length;
            if (!PInvoke.QueryFullProcessImageName(process, 0, buffer, ref size))
            {
                return string.Empty;
            }

            return Path.GetFileName(buffer[..(int)size].ToString());
        }
        catch (COMException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// <c>ISimpleAudioVolume.SetMasterVolume</c>'s <c>LPCGUID EventContext</c> parameter is
    /// confirmed (build-verified) to be generated as an unsafe <c>Guid*</c>, matching the
    /// pointer-parameter pattern this file already uses for <see cref="IAudioSessionEvents"/>.
    /// </summary>
    private static unsafe void WriteVolume(ISimpleAudioVolume volume, float level)
    {
        Guid ctx = EngineEventContext;
        volume.SetMasterVolume(level, &ctx);
    }

    private static HashSet<string> ToOrdinalIgnoreCaseSet(IReadOnlyCollection<string> names)
        => new(names, StringComparer.OrdinalIgnoreCase);

    private static void ReleaseIfComObject(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }
}
