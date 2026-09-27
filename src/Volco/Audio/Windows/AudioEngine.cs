using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio.Endpoints;
using Windows.Win32.System.Com;
using Volco.Audio;

namespace Volco.Audio.Windows;

/// <summary>
/// The real Core Audio engine (spec §2). Owns one dedicated MTA thread; every COM activation,
/// registration and write happens on it (spec §2.1). Callers post work through
/// <see cref="AudioCommand"/> values and never touch COM.
///
/// Wires together <see cref="DeviceRegistry"/> and <see cref="SessionRegistry"/>: both already
/// implement the Core Audio interop (enumeration, notification registration, the
/// enumerate-before-register ordering, cached device state) - this class's job is thread
/// ownership, the work queue, and composing the two registries' public surfaces into
/// <see cref="IAudioEngine"/>.
/// </summary>
public sealed class AudioEngine : IAudioEngine
{
    /// <summary>
    /// Alias for <see cref="SessionRegistry.EngineEventContext"/> - the SAME GUID value, not a
    /// second one. (The task brief's illustrative skeleton showed a separate
    /// "AudioEngine.EventContext = Guid.NewGuid()" field; the binding task instructions explicitly
    /// override that and require reusing <see cref="SessionRegistry.EngineEventContext"/> for
    /// device-level writes too, so the engine can recognise its own change notifications - spec
    /// §2.3 invariant 6. This alias exists only so a reader who remembers the brief's name can
    /// find the value; every write in this file references
    /// <see cref="SessionRegistry.EngineEventContext"/> directly.)
    /// </summary>
    internal static Guid EventContext => SessionRegistry.EngineEventContext;

    private readonly BlockingCollection<AudioCommand> _queue = new();
    private readonly ManualResetEventSlim _ready = new(initialState: false);

    private Thread? _thread;
    private DeviceRegistry? _deviceRegistry;
    private SessionRegistry? _sessionRegistry;
    private DefaultDevicePolicy? _defaultDevicePolicy;
    private bool _started;
    private bool _disposed;

    public event EventHandler<DevicesChangedEventArgs>? DevicesChanged;
    public event EventHandler<SessionsChangedEventArgs>? SessionsChanged;
    public event EventHandler<DeviceVolumeChangedEventArgs>? DeviceVolumeChanged;

    public AudioEngine()
    {
    }

    /// <summary>
    /// Starts the dedicated audio thread, which calls <c>CoInitializeEx(COINIT_MULTITHREADED)</c>,
    /// builds both registries, calls <see cref="DeviceRegistry.Start"/>, attaches
    /// <see cref="SessionRegistry"/> to every active render device found, and only then begins
    /// consuming the work queue. Blocks (bounded to 10s) until that initial setup has completed,
    /// so <see cref="GetDevices"/>/<see cref="GetSessions"/>/etc. are safe to call as soon as this
    /// method returns - none of IAudioEngine's query members are documented as "call only after
    /// some async ready signal," so Start() absorbs that wait itself rather than pushing it onto
    /// every caller.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the audio thread signalled ready within the timeout;
    /// <see langword="false"/> if it did not (the thread keeps running and may still become ready
    /// later, but query methods may return empty results until then). Callers that need to know
    /// whether startup actually succeeded - rather than just "not yet timed out" - should use this
    /// return value instead of inferring it from query results, which cannot distinguish "not
    /// ready yet" from "ready, but there is nothing to report."
    /// </returns>
    public bool Start()
    {
        if (_started)
        {
            return _ready.IsSet;
        }

        _started = true;

        _thread = new Thread(RunAudioThread)
        {
            IsBackground = true,
            Name = "Volco Audio Engine",
        };
        _thread.Start();

        bool ready = _ready.Wait(TimeSpan.FromSeconds(10));
        if (!ready)
        {
            Volco.AppLog.Error("AudioEngine.Start: audio thread did not signal ready within 10s.");
        }

        return ready;
    }

    // ── Queries - served from the in-memory registries, safe from any thread ──────────────────

    public IReadOnlyList<AudioDevice> GetDevices(AudioFlow flow)
        => _deviceRegistry?.GetDevices(flow) ?? Array.Empty<AudioDevice>();

    public AudioDevice? GetDefaultDevice(AudioFlow flow, AudioRole role)
        => _deviceRegistry?.GetDefault(flow, role);

    public IReadOnlyList<AudioSessionInfo> GetSessions()
        => _sessionRegistry?.GetSessions() ?? Array.Empty<AudioSessionInfo>();

    /// <summary>
    /// Reads the cached snapshot on <see cref="DeviceRegistry.DeviceHandle"/> directly - never a
    /// COM call, never posted to the audio thread. This is what makes it safe to call from the UI
    /// thread and from <c>ControllerStateReporter</c> (task requirement 4 / spec).
    /// </summary>
    public bool TryGetDeviceState(DeviceRef device, out float level, out bool muted)
    {
        var handle = _deviceRegistry?.Resolve(device);
        if (handle is null)
        {
            level = 0f;
            muted = false;
            return false;
        }

        level = handle.CachedLevel;
        muted = handle.CachedMuted;
        return true;
    }

    // ── Writes - struct commands posted to the audio thread, never a closure per call ──────────

    public void SetDeviceVolume(DeviceRef device, float level)
        => Post(AudioCommand.SetDeviceVolume(device, level));

    public void SetDeviceMute(DeviceRef device, bool muted)
        => Post(AudioCommand.SetDeviceMute(device, muted));

    public void ToggleDeviceMute(DeviceRef device)
        => Post(AudioCommand.ToggleDeviceMute(device));

    public void SetSessionVolume(SessionFilter filter, float level)
    {
        ArgumentNullException.ThrowIfNull(filter);
        Post(AudioCommand.SetSessionVolume(filter, level));
    }

    public void SetDefaultDevice(string endpointId, AudioFlow flow)
        => Post(AudioCommand.SetDefaultDevice(endpointId, flow));

    private void Post(AudioCommand command)
    {
        try
        {
            _queue.Add(command);
        }
        catch (InvalidOperationException)
        {
            // The queue has already had CompleteAdding() called (engine disposing/disposed);
            // drop the command rather than throw into a caller that has no reason to expect it.
        }
    }

    /// <summary>
    /// Passed to both registries as <c>onPendingWork</c>. May be invoked from an arbitrary RPC
    /// thread (spec §2.1) - must be non-blocking, no COM, no lock. Posting a
    /// <see cref="AudioCommand.Drain"/> onto an unbounded <see cref="BlockingCollection{T}"/> is a
    /// plain enqueue, satisfying that. Both registries' <c>DrainPendingChanges</c> are safe to call
    /// with nothing pending (a harmless no-op loop over an empty queue), so redundant wake-ups -
    /// one call to this per queued change, never batched - are tolerated for free.
    /// </summary>
    private void PostDrain()
    {
        try
        {
            _queue.Add(AudioCommand.Drain);
        }
        catch (InvalidOperationException)
        {
            // Engine disposing/disposed; nothing left to wake for.
        }
    }

    // ── The audio thread ────────────────────────────────────────────────────────────────────

    private unsafe void RunAudioThread()
    {
        HRESULT hr = PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
        if (hr.Value < 0)
        {
            Volco.AppLog.Error($"AudioEngine: CoInitializeEx failed with 0x{hr.Value:X8}.");
        }

        try
        {
            _deviceRegistry = new DeviceRegistry(OnDeviceRegistryChanged, OnDeviceVolumeChanged, PostDrain, SessionRegistry.EngineEventContext);
            _sessionRegistry = new SessionRegistry(OnSessionRegistryChanged, PostDrain);
            _defaultDevicePolicy = new DefaultDevicePolicy();

            _deviceRegistry.Start();

            // Requirement: after DeviceRegistry.Start(), attach the session registry to every
            // active render device found at startup - not just the default one (spec §2.3
            // invariant 5).
            foreach (var device in _deviceRegistry.GetDevices(AudioFlow.Render))
            {
                AttachRenderDevice(device.Id);
            }

            _ready.Set();

            foreach (var command in _queue.GetConsumingEnumerable())
            {
                try
                {
                    Process(command);
                }
                catch (COMException ex)
                {
                    Volco.AppLog.Error($"AudioEngine: command {command.Kind} failed: {ex.Message}");
                }
                catch (Exception ex)
                {
                    // This is a raw background Thread - an unhandled exception here terminates
                    // the process by default, which is exactly the reliability regression Option D
                    // exists to remove. A dropped command that is logged is strictly better than a
                    // dead tray app, so every command is isolated: log and keep consuming the next
                    // one, whatever kind of exception a Do*/Process handler throws.
                    Volco.AppLog.Error($"AudioEngine: command {command.Kind} threw an unexpected exception: {ex}");
                }
            }
        }
        finally
        {
            _sessionRegistry?.Dispose();
            _deviceRegistry?.Dispose();
            PInvoke.CoUninitialize();
        }
    }

    private void Process(AudioCommand command)
    {
        switch (command.Kind)
        {
            case AudioCommandKind.Drain:
                _deviceRegistry!.DrainPendingChanges();
                _sessionRegistry!.DrainPendingChanges();
                break;

            case AudioCommandKind.SetDeviceVolume:
                DoSetDeviceVolume(command.Device, command.Level);
                break;

            case AudioCommandKind.SetDeviceMute:
                DoSetDeviceMute(command.Device, command.Muted);
                break;

            case AudioCommandKind.ToggleDeviceMute:
                DoToggleDeviceMute(command.Device);
                break;

            case AudioCommandKind.SetSessionVolume:
                if (command.Filter is not null)
                {
                    _sessionRegistry!.ApplyVolume(command.Filter, command.Level);
                }

                break;

            case AudioCommandKind.SetDefaultDevice:
                if (command.Text is not null)
                {
                    DoSetDefaultDevice(command.Text, command.Flow);
                }

                break;
        }
    }

    /// <summary>
    /// Attaches <see cref="SessionRegistry"/> to one render device by id, resolving it back to the
    /// <see cref="DeviceRegistry.DeviceHandle"/> that <see cref="SessionRegistry.Attach"/> needs.
    /// Used both for the initial device list in <see cref="RunAudioThread"/> and for a later
    /// <see cref="DeviceChangeKind.Added"/> notification in <see cref="OnDeviceRegistryChanged"/>.
    /// A capture device or an id that no longer resolves is silently skipped -
    /// <see cref="SessionRegistry.Attach"/> itself already no-ops for a capture device's null
    /// <c>SessionManager</c>.
    /// </summary>
    private void AttachRenderDevice(string deviceId)
    {
        var handle = _deviceRegistry!.Resolve(DeviceRef.ById(deviceId, AudioFlow.Render));
        if (handle is null)
        {
            return;
        }

        try
        {
            _sessionRegistry!.Attach(handle);
        }
        catch (COMException ex)
        {
            Volco.AppLog.Error($"AudioEngine: failed to attach session registry to device '{deviceId}': {ex.Message}");
        }
    }

    /// <summary>
    /// Raised by <see cref="DeviceRegistry"/> from inside <see cref="DeviceRegistry.DrainPendingChanges"/>
    /// - i.e. already on the audio thread, so it is safe to do COM work here directly (Attach/
    /// Detach) rather than posting another command.
    /// </summary>
    private void OnDeviceRegistryChanged(DevicesChangedEventArgs args)
    {
        if (args.Device.Flow == AudioFlow.Render)
        {
            if (args.Kind == DeviceChangeKind.Added)
            {
                AttachRenderDevice(args.Device.Id);
            }
            else if (args.Kind == DeviceChangeKind.Removed)
            {
                _sessionRegistry!.Detach(args.Device.Id);
            }
        }

        DevicesChanged?.Invoke(this, args);
    }

    private void OnSessionRegistryChanged(SessionsChangedEventArgs args)
        => SessionsChanged?.Invoke(this, args);

    /// <summary>
    /// Raised by <see cref="DeviceRegistry"/> from inside <see cref="DeviceRegistry.DrainPendingChanges"/>
    /// for a volume/mute change on any tracked device - whether this engine wrote it
    /// (<see cref="DeviceVolumeChangedEventArgs.IsOwnChange"/> true, computed by
    /// <see cref="DeviceRegistry"/> against the <see cref="SessionRegistry.EngineEventContext"/>
    /// passed into its constructor) or it originated externally (the Windows volume mixer, another
    /// app) - closing spec §3 item 3. Just re-raises the already-fully-formed args; already on the
    /// audio thread, so no marshaling needed here.
    /// </summary>
    private void OnDeviceVolumeChanged(DeviceVolumeChangedEventArgs args)
        => DeviceVolumeChanged?.Invoke(this, args);

    private void DoSetDeviceVolume(DeviceRef device, float level)
    {
        var handle = _deviceRegistry!.Resolve(device);
        if (handle is null)
        {
            Volco.AppLog.Error($"AudioEngine.SetDeviceVolume: could not resolve device for {device}.");
            return;
        }

        float clamped = Math.Clamp(level, 0f, 1f);
        try
        {
            WriteDeviceVolume(handle.Volume, clamped);
            UpdateCachedDeviceState(handle, clamped, handle.CachedMuted);
        }
        catch (COMException ex)
        {
            Volco.AppLog.Error($"AudioEngine.SetDeviceVolume: write failed for device '{handle.Id}': {ex.Message}");
        }
    }

    private void DoSetDeviceMute(DeviceRef device, bool muted)
    {
        var handle = _deviceRegistry!.Resolve(device);
        if (handle is null)
        {
            Volco.AppLog.Error($"AudioEngine.SetDeviceMute: could not resolve device for {device}.");
            return;
        }

        try
        {
            WriteDeviceMute(handle.Volume, muted);
            UpdateCachedDeviceState(handle, handle.CachedLevel, muted);
        }
        catch (COMException ex)
        {
            Volco.AppLog.Error($"AudioEngine.SetDeviceMute: write failed for device '{handle.Id}': {ex.Message}");
        }
    }

    private void DoToggleDeviceMute(DeviceRef device)
    {
        var handle = _deviceRegistry!.Resolve(device);
        if (handle is null)
        {
            Volco.AppLog.Error($"AudioEngine.ToggleDeviceMute: could not resolve device for {device}.");
            return;
        }

        bool next = !handle.CachedMuted;
        try
        {
            WriteDeviceMute(handle.Volume, next);
            UpdateCachedDeviceState(handle, handle.CachedLevel, next);
        }
        catch (COMException ex)
        {
            Volco.AppLog.Error($"AudioEngine.ToggleDeviceMute: write failed for device '{handle.Id}': {ex.Message}");
        }
    }

    private void DoSetDefaultDevice(string endpointId, AudioFlow flow)
    {
        bool ok = _defaultDevicePolicy!.TrySetDefault(endpointId);
        if (!ok)
        {
            Volco.AppLog.Error($"AudioEngine.SetDefaultDevice: failed to set default endpoint '{endpointId}' (flow {flow}).");
        }
    }

    /// <summary>
    /// Eagerly updates the cached snapshot right after this engine's own write succeeds, so a
    /// <see cref="TryGetDeviceState"/> call immediately after does not race the asynchronous
    /// <see cref="IAudioEndpointVolumeCallback"/> notification for the same change landing on the
    /// audio thread a little later.
    ///
    /// Does NOT raise <see cref="DeviceVolumeChanged"/> itself any more - that would double-fire
    /// alongside the real notification. <see cref="DeviceRegistry"/>'s endpoint-volume callback now
    /// enqueues every notification (this engine's own writes included) and
    /// <see cref="OnDeviceVolumeChanged"/> re-raises it with an accurate
    /// <see cref="DeviceVolumeChangedEventArgs.IsOwnChange"/> - computed by
    /// <see cref="DeviceRegistry"/> against the <see cref="SessionRegistry.EngineEventContext"/>
    /// passed into its constructor - covering both this engine's own writes and externally
    /// originated changes (spec §3 item 3) through one path.
    /// </summary>
    private static void UpdateCachedDeviceState(DeviceRegistry.DeviceHandle handle, float level, bool muted)
    {
        handle.CachedLevel = level;
        handle.CachedMuted = muted;
    }

    /// <summary>
    /// <c>IAudioEndpointVolume.SetMasterVolumeLevelScalar</c>'s <c>LPCGUID pguidEventContext</c>
    /// parameter is UNPROVEN (no local compiling precedent calls this method) - inferred by direct
    /// analogy to <c>ISimpleAudioVolume.SetMasterVolume</c>, which SessionRegistry.cs:719 confirms
    /// (build-verified) takes an unsafe <c>Guid*</c> for the same role. Both are standard Core
    /// Audio "set with event context" methods from the same family; same shape is expected but not
    /// verified for this specific member.
    /// </summary>
    private static unsafe void WriteDeviceVolume(IAudioEndpointVolume volume, float level)
    {
        Guid ctx = SessionRegistry.EngineEventContext;
        volume.SetMasterVolumeLevelScalar(level, &ctx);
    }

    /// <summary>
    /// <c>IAudioEndpointVolume.SetMute</c> is UNPROVEN like <see cref="WriteDeviceVolume"/>, same
    /// analogy. Its first parameter is declared <see cref="BOOL"/> (not <c>bool</c>) by inference
    /// from <c>IAudioEndpointVolume.GetMute(out BOOL)</c>, which DeviceRegistry.cs:407/422 confirms
    /// (build-verified) already uses <see cref="BOOL"/> for this interface's mute value - i.e. this
    /// interface's generated shape does not get a bool-friendly overload the way some other CsWin32
    /// members do, so its setter is expected to match. The <c>BOOL(bool)</c> constructor itself is
    /// the standard, near-universal shape of CsWin32's generated BOOL type but is NOT proven by any
    /// local precedent - see the report.
    /// </summary>
    private static unsafe void WriteDeviceMute(IAudioEndpointVolume volume, bool muted)
    {
        Guid ctx = SessionRegistry.EngineEventContext;
        volume.SetMute(new BOOL(muted), &ctx);
    }

    /// <summary>
    /// Drains the queue (via <see cref="BlockingCollection{T}.CompleteAdding"/>, which lets
    /// <see cref="RunAudioThread"/>'s <c>GetConsumingEnumerable</c> finish yielding every
    /// already-posted command before it ends), which in turn disposes both registries - they
    /// unregister their own callbacks in their own <c>Dispose</c> - stops the thread, and only then
    /// runs <c>CoUninitialize</c> (in <see cref="RunAudioThread"/>'s <c>finally</c>, after the loop
    /// exits). Safe to call whether or not <see cref="Start"/> was ever called.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _queue.CompleteAdding();
        _thread?.Join();
        _queue.Dispose();
        _ready.Dispose();
    }

    private enum AudioCommandKind
    {
        Drain,
        SetDeviceVolume,
        SetDeviceMute,
        ToggleDeviceMute,
        SetSessionVolume,
        SetDefaultDevice,
    }

    /// <summary>
    /// One posted unit of work for the audio thread. A struct, not a closure - the ~50Hz slider
    /// path (spec §2.1, §1) must not allocate per tick. <see cref="Filter"/> is the one
    /// reference-typed field; it is already allocated by the caller for a
    /// <see cref="SetSessionVolume"/> call (e.g. a fresh <c>SessionFilter.ByImageNames</c> built by
    /// <c>SliderProcessor</c> each tick to describe the target set) - carrying that same reference
    /// through the struct costs nothing extra here, it is not an allocation this type introduces.
    /// </summary>
    private readonly record struct AudioCommand(
        AudioCommandKind Kind,
        DeviceRef Device,
        float Level,
        bool Muted,
        SessionFilter? Filter,
        string? Text,
        AudioFlow Flow)
    {
        public static readonly AudioCommand Drain =
            new(AudioCommandKind.Drain, default, default, default, null, null, default);

        public static AudioCommand SetDeviceVolume(DeviceRef device, float level) =>
            new(AudioCommandKind.SetDeviceVolume, device, level, default, null, null, default);

        public static AudioCommand SetDeviceMute(DeviceRef device, bool muted) =>
            new(AudioCommandKind.SetDeviceMute, device, default, muted, null, null, default);

        public static AudioCommand ToggleDeviceMute(DeviceRef device) =>
            new(AudioCommandKind.ToggleDeviceMute, device, default, default, null, null, default);

        public static AudioCommand SetSessionVolume(SessionFilter filter, float level) =>
            new(AudioCommandKind.SetSessionVolume, default, level, default, filter, null, default);

        public static AudioCommand SetDefaultDevice(string endpointId, AudioFlow flow) =>
            new(AudioCommandKind.SetDefaultDevice, default, default, default, null, endpointId, flow);
    }
}
