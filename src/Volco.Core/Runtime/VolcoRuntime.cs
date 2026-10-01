using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Diagnostics;
using Volco.Core.Input;
using Volco.Core.Input.Actions;

namespace Volco.Core.Runtime;

/// <summary>
/// Everything Volco does that is not a window: transport construction and restart, inbound
/// message routing to <see cref="SliderProcessor"/> / <see cref="ButtonPressDetector"/>,
/// <see cref="ActionFactory"/> dispatch, the pending-mute-OSD correlation, controller-state
/// (LED) pushes with change detection, and the post-connect re-push mitigations. Headless: a UI
/// head (the Avalonia head, Volco.App) constructs one, calls <see cref="Start"/>, and renders
/// what the events publish.
/// </summary>
/// <remarks>
/// Threading: every event on this class may be raised on ANY thread - the audio thread
/// (engine callbacks), a transport's receive/timer threads, the ConfigManager debounce-timer
/// thread, the SliderProcessor coalescing timer, the ButtonPressDetector timers, or the
/// post-connect re-push timers. Subscribers marshal to their UI thread themselves.
/// </remarks>
public sealed class VolcoRuntime : IDisposable, IPresetController
{
    /// <summary>The live preset's id as last set; null until the first set. Read through
    /// <see cref="ActivePresetId"/>, which falls back to the first preset when this one is gone
    /// (deleted in the settings UI) or was never set.</summary>
    private string? _activePresetId;

    private readonly bool _audioReady;
    private readonly Func<ConnectionConfig, IControllerConnection?> _connectionFactory;
    private readonly VolcoRuntimeOptions _options;

    /// <summary>
    /// The current transport. Ownership handoff: whoever removes a transport from this field
    /// with an Interlocked operation (StartConnection when replacing it, or Dispose) is the one
    /// and only party that detaches and disposes it. It is only ever written with
    /// Interlocked.Exchange / CompareExchange, so a restart on the ConfigManager debounce thread
    /// and a concurrent Dispose can never both dispose the same transport, and a transport a
    /// racing restart publishes is always found by exactly one of them. Readers
    /// (PushControllerState, ConnectionStatus, the sender filter) read it once and tolerate
    /// null.
    /// </summary>
    private IControllerConnection? _connection;
    private ButtonPressDetector? _buttonPressDetector;
    private SliderProcessor? _sliderProcessor;
    private bool _started;

    /// <summary>
    /// 0/1 flag, set first thing in <see cref="Dispose"/> via Interlocked.Exchange (a full fence,
    /// which the ownership handoff on <see cref="_connection"/> relies on) and checked through
    /// <see cref="IsDisposed"/> before raising any event, starting a transport, dispatching a
    /// button action or pushing controller state. Not a lock: it only stops work that starts
    /// after Dispose has begun. A callback already past its check (e.g. a timer callback queued
    /// on the thread pool before its timer was disposed) can still finish, which is the same
    /// unavoidable race the pre-runtime Shutdown() documented.
    /// </summary>
    private int _disposed;

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Set right before <see cref="ActionFactory.Create"/>'s action for a mute-type button press
    /// is executed, cleared by <see cref="OnDeviceVolumeChanged"/> once ITS OWN write for the
    /// expected device lands. Execute() only posts a command to the audio thread (review item 3)
    /// - reading state synchronously right after it returns, as this used to, sees pre-write
    /// state. Matched by device id (not just "any change") so a press against a device that
    /// never actually resolves (e.g. an unplugged headset - see review item 2) does not leave a
    /// dangling flag that pops an unrelated OSD the next time some other device's volume changes.
    /// </summary>
    private sealed record PendingMuteOsd(string DeviceId, ButtonActionConfig Config);
    private PendingMuteOsd? _pendingMuteOsd;

    /// <summary>
    /// Same idea for a SetOutputDevice/ToggleOutputDevice press: the switch is posted to the
    /// audio thread, so the OSD waits for the authoritative <see cref="DeviceChangeKind.DefaultChanged"/>
    /// for the target endpoint. Read/written with Volatile/Interlocked (handlers run on the
    /// detector and audio threads, no lock) so that of the up-to-3 per-role DefaultChanged events
    /// exactly one wins the compare-exchange and raises the OSD.
    /// </summary>
    private sealed record PendingDeviceOsd(string TargetId, long DeadlineTickMs);
    private PendingDeviceOsd? _pendingDeviceOsd;

    /// <summary>
    /// Guards <see cref="_lastPushedFrames"/> (Bug 4). PushControllerState can be entered from
    /// several threads: the audio thread (OnDeviceVolumeChanged/OnDevicesChangedForControllerState,
    /// both invoked from the platform audio engine's dedicated thread), whatever thread raises
    /// IControllerConnection.StatusChanged (OnConnectionStatusChanged) — for SerialConnection/
    /// UdpConnection that can be the thread that called <see cref="Start"/> (the UI thread), the
    /// ConfigManager debounce-timer thread (StartConnection via OnConfigChanged — see Bug 1), or
    /// UdpConnection's own receive-loop/inactivity-timer threads — plus a transport receive thread
    /// (OnMessageReceived) and the post-connect re-push timers. The lock only ever protects
    /// comparing/replacing this field — it is never held across a call into the engine or into
    /// the connection's Send.
    /// </summary>
    private readonly object _pushStateLock = new();
    private IReadOnlyList<byte[]>? _lastPushedFrames;

    /// <summary>
    /// Connect-race fix: on a USB replug, the transport (e.g. SerialConnection) can report
    /// Connected within milliseconds of Windows enumerating the CDC device, while the ESP32 is
    /// still executing setup() and not yet reading serial — the immediate PushControllerState in
    /// OnConnectionStatusChanged is sent into a void, leaving the controller's own boot-time LED
    /// state (both mute LEDs lit — see firmware note) uncorrected until some later event.
    ///
    /// Two independent, additive mitigations, both scheduled/armed from OnConnectionStatusChanged
    /// on every transition to Connected:
    ///   (a) _postConnectTimers — one-shot re-pushes, one per entry in
    ///       <see cref="VolcoRuntimeOptions.PostConnectRepushDelays"/> (750ms and 2500ms by
    ///       default), that don't depend on the controller doing anything. Guarded by
    ///       _postConnectLock so a second connect (replug twice quickly) disposes the previous
    ///       set before arming a new one, rather than stacking timers.
    ///   (b) _awaitingFirstMessageAfterConnect — set on every Connected transition, consumed
    ///       (via Interlocked.Exchange) by the first MessageReceived after it, which is positive
    ///       proof the firmware's serial reader is alive. Self-timing, but can't stand alone since
    ///       an untouched controller (no slider movement past the deadband, no button press) may
    ///       never send anything.
    /// Both paths call PushControllerState(force: true), the same call OnConnectionStatusChanged
    /// already makes — force bypasses _lastPushedFrames change-detection but pushes are otherwise
    /// idempotent, so a redundant push (e.g. both (a) and (b) firing) is harmless.
    /// </summary>
    private readonly object _postConnectLock = new();
    private System.Threading.Timer[]? _postConnectTimers;
    private int _awaitingFirstMessageAfterConnect;

    /// <param name="config">Already loaded by the head.</param>
    /// <param name="audio">The platform engine, already started. Owned by the head, not by this
    /// runtime.</param>
    /// <param name="audioReady">What the platform engine's Start() returned. False → no config
    /// migration, AudioEngineFailed = true; everything else still starts.</param>
    /// <param name="connectionFactory">Null → the real Serial/Udp/WebSocket switch. Tests inject fakes.</param>
    /// <param name="options">Null → <see cref="VolcoRuntimeOptions.Default"/>.</param>
    public VolcoRuntime(ConfigManager config, IAudioEngine audio, bool audioReady,
                        Func<ConnectionConfig, IControllerConnection?>? connectionFactory = null,
                        VolcoRuntimeOptions? options = null)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        Audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _audioReady = audioReady;
        _connectionFactory = connectionFactory
                             ?? new Func<ConnectionConfig, IControllerConnection?>(CreateDefaultConnection);
        _options = options ?? VolcoRuntimeOptions.Default;
    }

    public IAudioEngine Audio { get; }
    public ConfigManager Config { get; }
    public bool AudioEngineFailed => !_audioReady;

    /// <summary>The current transport's status; Disconnected when there is no transport.</summary>
    public ConnectionStatus ConnectionStatus => Volatile.Read(ref _connection)?.Status ?? ConnectionStatus.Disconnected;

    /// <summary>
    /// Fix D: the most recently processed volume [0,1] for a slider index, or null before
    /// <see cref="Start"/>, after <see cref="Dispose"/>, or when that slider has never reported a
    /// value yet (the firmware only sends a slider's value when it moves, so this can genuinely
    /// be unknown for a long time after app start). Additive: lets a UI seed its live-level
    /// display from state the runtime already holds instead of waiting on
    /// <see cref="SliderLevelChanged"/> alone, which a window hidden since before a slider last
    /// moved would otherwise never receive.
    /// </summary>
    public float? GetSliderLevel(int sliderIndex)
        => IsDisposed ? null : _sliderProcessor?.GetLastLevel(sliderIndex);

    // All events may be raised on ANY thread (audio thread, transport threads, timer threads).
    // Subscribers marshal to their UI thread themselves.

    /// <summary>Slider volume OSD (re-raised from SliderProcessor) and mute OSD (ShowMuteOsd).</summary>
    public event EventHandler<VolumeChangeNotification>? VolumeChanged;

    /// <summary>Re-raised from <see cref="SliderProcessor.SliderLevelChanged"/>.</summary>
    public event EventHandler<SliderLevelEventArgs>? SliderLevelChanged;

    /// <summary>
    /// Status of whichever transport is current. Survives transport restarts (Review Focus #2):
    /// the runtime attaches its handler to each transport it creates and detaches it from the old
    /// one on restart, so a subscriber never has to re-subscribe to a new IControllerConnection.
    /// </summary>
    public event EventHandler<ConnectionStatus>? ConnectionStatusChanged;

    /// <summary>Every <see cref="IAudioEngine.DevicesChanged"/>, of every kind, forwarded.</summary>
    public event EventHandler<DevicesChangedEventArgs>? DevicesChanged;

    /// <summary>The live preset changed (argument: its id), from a button or from <see cref="SetActivePreset"/>.</summary>
    public event EventHandler<string>? ActivePresetChanged;

    /// <summary>A button on the controller was pressed down (argument: its hardware input),
    /// whether or not it is configured. Lets a UI show which card is which physical button.</summary>
    public event EventHandler<int>? ButtonActivity;

    /// <inheritdoc/>
    public string ActivePresetId
    {
        get
        {
            var presets = Config.Config.Presets;
            var id = Volatile.Read(ref _activePresetId);
            if (id != null && presets.Exists(p => p.Id == id)) return id;
            return presets.Count > 0 ? presets[0].Id : AppConfig.DefaultPresetId;
        }
    }

    /// <inheritdoc/>
    public void SetActivePreset(string presetId)
    {
        if (IsDisposed) return;
        if (!Config.Config.Presets.Exists(p => p.Id == presetId)) return;
        var previous = ActivePresetId;
        Volatile.Write(ref _activePresetId, presetId);
        if (previous == presetId) return;

        AppLog.Info($"Active preset: {presetId}");
        ActivePresetChanged?.Invoke(this, presetId);
        PushControllerState();
    }

    /// <inheritdoc/>
    public void NextPreset()
    {
        var presets = Config.Config.Presets;
        if (presets.Count == 0) return;
        var index = presets.FindIndex(p => p.Id == ActivePresetId);
        SetActivePreset(presets[(index + 1) % presets.Count].Id);
    }

    /// <summary>Drops the current transport and starts a fresh one from the current connection
    /// config (the settings UI's Reconnect button).</summary>
    public void Reconnect()
    {
        if (IsDisposed || !_started) return;
        AppLog.Info("Reconnect requested");
        StartConnection();
    }

    /// <summary>Runs one action now, as a button press would (the settings UI's Test button).
    /// Preset actions change the live preset.</summary>
    public void Execute(ButtonActionConfig action)
    {
        if (IsDisposed) return;
        ActionFactory.Create(action, Audio, this)?.Execute();
    }

    /// <summary>
    /// Migrates v1→v2 config if needed and audioReady; subscribes to the engine; creates
    /// SliderProcessor + ButtonPressDetector; starts the transport. Call once.
    /// </summary>
    public void Start()
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(VolcoRuntime));
        if (_started) throw new InvalidOperationException("VolcoRuntime.Start() may only be called once.");
        _started = true;

        Audio.DeviceVolumeChanged += OnDeviceVolumeChanged;
        Audio.DevicesChanged += OnDevicesChangedForControllerState;

        if (!_audioReady)
        {
            AppLog.Error("AudioEngine.Start() did not signal ready within its timeout; the audio " +
                         "thread likely failed to come up. Volco will run but will not be able to " +
                         "control audio until this is resolved — see volco.log.");
        }

        // Skipped entirely when the engine isn't ready (review item 4): DeviceRegistry.Start()
        // may still be running on the audio thread past the timeout, and GetDevices() here would
        // race its unsynchronised-at-the-time dictionary writes. Calling MigrateFile with an
        // empty device list is also a data-loss trap in its own right (review item 5) - it
        // reduces to a no-op below anyway, but skipping the call here avoids depending on that.
        if (_audioReady && ConfigMigrator.NeedsMigration(Config.Config))
        {
            AppLog.Info($"Migrating config to schema v{AppConfig.CurrentSchemaVersion}");
            ConfigMigrator.MigrateFile(Config.ConfigPath, Config.Config,
                                       Audio.GetDevices(AudioFlow.Render));
            Config.Save();
        }

        // Fix C: repair any DeviceRefConfig left with a blank id (e.g. Preserve() during v1
        // migration when the device was absent, or a v2 config with an id manually removed) now
        // that the device list is available. Same audioReady guard as migration above, for the
        // same reason (GetDevices() would race DeviceRegistry.Start() otherwise).
        if (_audioReady && ConfigMigrator.HealDeviceIds(Config.Config, Audio.GetDevices(AudioFlow.Render)))
        {
            AppLog.Info("Healed device id(s) from a friendly-name match at startup");
            Config.Save();
        }

        Config.ConnectionConfigChanged += OnConfigChanged;

        // Input processing
        _buttonPressDetector = new ButtonPressDetector();
        _buttonPressDetector.PressDetected += OnButtonPressDetected;
        _sliderProcessor = new SliderProcessor(Audio, Config);
        _sliderProcessor.VolumeChanged += OnSliderVolumeChanged;
        _sliderProcessor.SliderLevelChanged += OnSliderLevelChanged;

        // Connection
        StartConnection();
    }

    private static IControllerConnection? CreateDefaultConnection(ConnectionConfig config) => config.Type switch
    {
        ConnectionType.Serial => new SerialConnection(config.Serial),
        ConnectionType.Udp => new UdpConnection(config.Udp),
        ConnectionType.WebSocket => new WebSocketConnection(config.WebSocket),
        _ => null
    };

    /// <summary>
    /// Creates (or re-creates, on a connection-config change) the transport. Runs on the thread
    /// that called <see cref="Start"/>, or on the ConfigManager debounce-timer thread via
    /// <see cref="OnConfigChanged"/> (Bug 1).
    /// </summary>
    private void StartConnection()
    {
        if (IsDisposed) return;

        // Take ownership of the old transport (see _connection): if Dispose() got to it first,
        // this returns null and Dispose() retires it instead.
        var old = Interlocked.Exchange(ref _connection, null);
        if (old != null)
        {
            // Detached before Dispose() (Review Focus #2): the old transport can still raise
            // events from its background threads, and those must never be reported as the
            // status of the connection that replaces it. The Disconnected a real transport
            // raises from its own Dispose() therefore no longer reaches subscribers by itself,
            // so report it here, once, if they were last told something else - otherwise a
            // settings page would keep showing the old transport's "Connected" while e.g. a new
            // WebSocket listener sits Disconnected and raises nothing.
            var lastStatus = old.Status;
            RetireTransport(old);
            if (lastStatus != ConnectionStatus.Disconnected && !IsDisposed)
                ConnectionStatusChanged?.Invoke(this, ConnectionStatus.Disconnected);
        }

        var config = Config.Config.Connection;
        AppLog.Info($"Starting connection: {config.Type}");
        var connection = _connectionFactory(config);

        if (connection == null) return;

        connection.MessageReceived += OnMessageReceived;
        connection.StatusChanged += OnConnectionStatusChanged;

        // Publish. Anything displaced is a transport a concurrent StartConnection published in
        // the meantime; by the ownership rule, removing it makes it ours to retire.
        var displaced = Interlocked.Exchange(ref _connection, connection);
        if (displaced != null) RetireTransport(displaced);

        // Dispose() may have started after the IsDisposed check at the top. Both the publish
        // above and Dispose()'s flag write are full fences, so if this read still sees 0, any
        // Dispose() that starts later is guaranteed to find the transport in _connection and
        // retire it itself. If it sees 1, exactly one of us gets it out of the field: retire
        // it only if CompareExchange says it is still there, and never Connect() it.
        // Residual window, accepted: a Dispose() landing between this check and Connect() below
        // retires the transport, and Connect() then runs on (or concurrently with) its Dispose().
        // Closing it would need a lock held across Connect()/Dispose(), both of which raise
        // StatusChanged synchronously into subscribers - a deadlock risk for a UI that marshals
        // synchronously. Dispose() only happens at app exit, so the worst case is a listener
        // re-bound by a process that is about to end.
        if (IsDisposed)
        {
            if (Interlocked.CompareExchange(ref _connection, null, connection) == connection)
                RetireTransport(connection);
            return;
        }

        connection.Connect();
    }

    /// <summary>Detaches and disposes a transport this thread has taken ownership of by removing
    /// it from <see cref="_connection"/> (see its doc). Never called with a lock held.</summary>
    private void RetireTransport(IControllerConnection connection)
    {
        connection.MessageReceived -= OnMessageReceived;
        connection.StatusChanged -= OnConnectionStatusChanged;
        connection.Dispose();
    }

    private void OnConnectionStatusChanged(object? sender, ConnectionStatus status)
    {
        // Belt and braces for the restart path above: an old transport's handler invocation that
        // was already in flight on another thread when StartConnection detached it is dropped.
        if (IsDisposed || !ReferenceEquals(sender, Volatile.Read(ref _connection))) return;

        AppLog.Info($"Connection status changed: {status}");
        ConnectionStatusChanged?.Invoke(this, status);

        if (status == ConnectionStatus.Connected)
        {
            // force: true — a (re)connect means the controller's own LED state is unknown
            // (it may just have rebooted, or this may be a brand-new transport after Bug 1/2's
            // restart path), so always send the full state at least once regardless of whether
            // it matches the last state we happened to push to a previous connection.
            PushControllerState(force: true);
            // Connect-race fix (see field doc above): this immediate push can land before the
            // controller's own boot sequence is reading serial. Arm both mitigations for this
            // connect.
            ArmFirstMessagePush();
            ScheduleTimedRepushes();
        }
    }

    /// <summary>Arms mitigation (b): the very next MessageReceived, of any kind, triggers one
    /// forced re-push (consumed atomically so only the first message after this connect does).
    /// Runs on whatever thread raised StatusChanged — see class-level doc on _pushStateLock for
    /// the set of threads that can be.</summary>
    private void ArmFirstMessagePush() =>
        Interlocked.Exchange(ref _awaitingFirstMessageAfterConnect, 1);

    /// <summary>Arms mitigation (a): one-shot re-pushes at each of
    /// <see cref="VolcoRuntimeOptions.PostConnectRepushDelays"/> (~750ms and ~2500ms by default)
    /// after this connect. Disposes any set still pending from an earlier connect first, so
    /// replugging twice in quick succession re-arms rather than stacking timers. Runs on whatever
    /// thread raised StatusChanged; the timer callbacks themselves later run on a thread-pool
    /// thread.</summary>
    private void ScheduleTimedRepushes()
    {
        lock (_postConnectLock)
        {
            DisposePostConnectTimers();
            if (IsDisposed) return;

            var delays = _options.PostConnectRepushDelays;
            var timers = new System.Threading.Timer[delays.Count];
            for (int i = 0; i < delays.Count; i++)
            {
                timers[i] = new System.Threading.Timer(
                    _ => PushControllerState(force: true), null, delays[i], Timeout.InfiniteTimeSpan);
            }
            _postConnectTimers = timers;
        }
    }

    /// <summary>Caller holds <see cref="_postConnectLock"/>.</summary>
    private void DisposePostConnectTimers()
    {
        if (_postConnectTimers is null) return;
        foreach (var timer in _postConnectTimers)
            timer.Dispose();
        _postConnectTimers = null;
    }

    private void OnMessageReceived(object? sender, ControllerMessage msg)
    {
        if (IsDisposed) return;

        // Connect-race fix, mitigation (b): an inbound frame is proof the firmware's serial
        // reader is alive right now, regardless of whether it was ready when the immediate
        // post-connect push in OnConnectionStatusChanged went out. Interlocked.Exchange both
        // reads and clears the flag atomically so only the first message after a given connect
        // does this, not every message afterwards.
        if (Interlocked.Exchange(ref _awaitingFirstMessageAfterConnect, 0) == 1)
            PushControllerState(force: true);

        switch (msg)
        {
            case SliderMessage slider:
                AppLog.Debug($"Slider event: Index={slider.SliderIndex}, Value={slider.Value}");
                _sliderProcessor?.HandleSliderEvent(slider.SliderIndex, slider.Value);
                break;
            case ButtonMessage button:
                AppLog.Debug($"Button event: Index={button.ButtonIndex}, State={button.State}");
                if (button.State == ButtonState.Pressed)
                    ButtonActivity?.Invoke(this, button.ButtonIndex);
                var bcfg = Config.Config.Buttons.ElementAtOrDefault(button.ButtonIndex);
                // Fix A: no double-press action configured (in the live preset) -> pass 0, which
                // makes ButtonPressDetector fire Single synchronously on release instead of waiting
                // out a double-press window that can never be satisfied. Long-press is unaffected.
                var doublePressWindowMs = bcfg != null && bcfg.BindingFor(ActivePresetId).DoublePress.Action == ButtonActionType.None
                    ? 0
                    : bcfg?.DoublePressWindowMs ?? 300;
                _buttonPressDetector?.HandleButtonEvent(button.ButtonIndex, button.State,
                    bcfg?.LongPressMs ?? 500, doublePressWindowMs);
                break;
        }
    }

    private void OnButtonPressDetected(object? sender, ButtonPressEventArgs e)
    {
        if (IsDisposed) return;

        AppLog.Info($"Button press detected: Index={e.ButtonIndex}, Type={e.PressType}");
        var config = Config.Config;
        if (e.ButtonIndex < 0 || e.ButtonIndex >= config.Buttons.Count) return;

        var binding = config.Buttons[e.ButtonIndex].BindingFor(ActivePresetId);
        var actionConfig = e.PressType switch
        {
            PressType.Single => binding.SinglePress,
            PressType.Double => binding.DoublePress,
            PressType.Long => binding.LongPress,
            _ => null
        };

        if (actionConfig == null) return;
        var action = ActionFactory.Create(actionConfig, Audio, this);

        // action.Execute() only posts a command to the audio thread - the write hasn't happened
        // yet when Execute() returns (review item 3). Reading TryGetDeviceState/GetDefaultDevice
        // synchronously right here, as this used to, shows pre-toggle state: every mute press
        // flashed the wrong OSD glyph and pushed the wrong controller LED. Record which device
        // this mute action targets (if any) and let OnDeviceVolumeChanged show the OSD once ITS
        // OWN write for that device actually lands, with authoritative state.
        // PushControllerState() itself is now driven entirely off
        // OnDeviceVolumeChanged/OnDevicesChangedForControllerState below, which also closes spec
        // §3 item 3 for a change that didn't originate from a button at all (e.g. the Windows
        // volume mixer) - DeviceVolumeChanged previously had no subscribers at all.
        var expectedDeviceId = IsMuteAction(actionConfig.Action) ? ResolveExpectedMuteDeviceId(actionConfig) : null;
        _pendingMuteOsd = expectedDeviceId is null ? null : new PendingMuteOsd(expectedDeviceId, actionConfig);

        // Output-device switch: compute the target with the actions' own logic BEFORE Execute()
        // (afterwards the default may already have moved). Any press replaces the pending record.
        PendingDeviceOsd? pendingSwitch = null;
        AudioDevice? alreadyCurrent = null;
        if (actionConfig.Action is ButtonActionType.SetOutputDevice or ButtonActionType.ToggleOutputDevice)
        {
            var current = Audio.GetDefaultDevice(AudioFlow.Render);
            var targetId = actionConfig.Action == ButtonActionType.SetOutputDevice
                ? SetOutputDeviceAction.ResolveTargetId(actionConfig.Device)
                : ToggleOutputDeviceAction.ResolveTargetId(current, actionConfig.Device, actionConfig.DeviceB);
            if (targetId is not null)
            {
                // Already the default: Windows raises no change, so confirm right away.
                if (current is not null && string.Equals(current.Id, targetId, StringComparison.OrdinalIgnoreCase))
                    alreadyCurrent = current;
                else if (Audio.GetDevices(AudioFlow.Render)
                             .Any(d => string.Equals(d.Id, targetId, StringComparison.OrdinalIgnoreCase)))
                    // Only a target that is present can land. An absent one (unplugged headset)
                    // would leave the record dangling and claim a later external switch.
                    pendingSwitch = new PendingDeviceOsd(
                        targetId, Environment.TickCount64 + (long)_options.PendingSwitchTimeout.TotalMilliseconds);
            }
        }
        Interlocked.Exchange(ref _pendingDeviceOsd, pendingSwitch);

        var presetBefore = ActivePresetId;
        action?.Execute();

        if (alreadyCurrent is not null) ShowDeviceOsd(alreadyCurrent.Id, alreadyCurrent.FriendlyName);

        // Preset actions run synchronously (no audio-thread round trip), so the new preset is
        // already live here.
        if (actionConfig.Action is ButtonActionType.NextPreset or ButtonActionType.GoToPreset)
        {
            var now = ActivePresetId;
            if (now != presetBefore)
            {
                var name = config.Presets.Find(p => p.Id == now)?.Name ?? now;
                RaiseVolumeChanged(new VolumeChangeNotification(name, 0f, false, OsdKind.Preset));
            }
        }
    }

    private static bool IsMuteAction(ButtonActionType type) => type is
        ButtonActionType.MuteCurrentOutput or
        ButtonActionType.MuteCurrentInput or
        ButtonActionType.MuteSpecificDevice;

    /// <summary>Best-effort endpoint id this mute action targets, resolved with the same
    /// read-only queries <see cref="ShowMuteOsd"/> already uses - null when it cannot be
    /// determined right now (e.g. an unconfigured or currently-disconnected device), in which
    /// case no OSD will be shown for this press at all, rather than a wrong one later.</summary>
    private string? ResolveExpectedMuteDeviceId(ButtonActionConfig actionConfig) => actionConfig.Action switch
    {
        ButtonActionType.MuteCurrentOutput =>
            Audio.GetDefaultDevice(AudioFlow.Render)?.Id,
        ButtonActionType.MuteCurrentInput =>
            Audio.GetDefaultDevice(AudioFlow.Capture)?.Id,
        ButtonActionType.MuteSpecificDevice => ResolveSpecificDeviceId(actionConfig.Device),
        _ => null,
    };

    private string? ResolveSpecificDeviceId(DeviceRefConfig? cfg)
    {
        if (cfg == null) return null;
        if (!string.IsNullOrEmpty(cfg.Id)) return cfg.Id;
        if (string.IsNullOrEmpty(cfg.FriendlyName)) return null;
        return Audio.GetDevices(AudioFlow.Render)
            .FirstOrDefault(d => string.Equals(d.FriendlyName, cfg.FriendlyName, StringComparison.OrdinalIgnoreCase))
            ?.Id;
    }

    /// <summary>
    /// Fired by <see cref="IAudioEngine.DeviceVolumeChanged"/> - on the audio thread, with
    /// authoritative post-write level/mute state, whether this app caused the change or it came
    /// in externally (e.g. the Windows volume mixer). Always refreshes controller LEDs (cheap,
    /// and the only way an externally-originated change ever reaches the controller). Shows the
    /// mute OSD only for this app's own write (<see cref="DeviceVolumeChangedEventArgs.IsOwnChange"/>)
    /// to the exact device a pending mute press targeted (see <see cref="_pendingMuteOsd"/>) - do
    /// not call back into the engine with a write here, only the read-only queries
    /// ShowMuteOsd/PushControllerState already make, which are documented safe from any thread;
    /// VolumeChanged subscribers (the UI's OSD) marshal to the UI thread themselves.
    /// </summary>
    private void OnDeviceVolumeChanged(object? sender, DeviceVolumeChangedEventArgs e)
    {
        if (IsDisposed) return;

        PushControllerState();

        var pending = _pendingMuteOsd;
        if (pending is not null && e.IsOwnChange && string.Equals(pending.DeviceId, e.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            _pendingMuteOsd = null;
            ShowMuteOsd(pending.Config);
        }
    }

    /// <summary>
    /// Fired by <see cref="IAudioEngine.DevicesChanged"/> - on the audio thread. Only
    /// <see cref="DeviceChangeKind.DefaultChanged"/> matters for the controller: it is the
    /// authoritative, post-write counterpart of a ToggleOutputDevice/SetOutputDevice button press
    /// (the same staleness problem DeviceVolumeChanged solves for mute/volume - see
    /// <see cref="OnDeviceVolumeChanged"/> - applies to the active-device LED too, since a
    /// default-device switch is posted to the audio thread the same way).
    /// </summary>
    private void OnDevicesChangedForControllerState(object? sender, DevicesChangedEventArgs e)
    {
        if (IsDisposed) return;

        // Bug 3: the settings UI's device pickers must refresh for every kind of device change
        // (added/removed/state/default), not just DefaultChanged — a replugged device should
        // show up in the pickers even when it isn't the new default. So every kind is forwarded
        // as DevicesChanged; subscribers marshal to the UI thread themselves (this handler runs
        // on the audio thread), and with no settings window open there are simply no subscribers.
        DevicesChanged?.Invoke(this, e);

        if (e.Kind is DeviceChangeKind.Added or DeviceChangeKind.StateChanged)
        {
            // Fix C: a device that was absent (blank-id reference) at Start()'s heal pass, or at
            // the last StateChanged, may now be present - re-resolve against the current device
            // list so output switching recovers without a restart. GetDevices/HealDeviceIds only
            // touch config in memory here; RequestSave() debounces the write, and no runtime lock
            // is held across it.
            if (ConfigMigrator.HealDeviceIds(Config.Config, Audio.GetDevices(AudioFlow.Render)))
            {
                AppLog.Info("Healed device id(s) after a device change");
                Config.RequestSave();
            }
        }

        if (e.Kind == DeviceChangeKind.DefaultChanged)
        {
            // Device-switch OSD. Clear the pending record FIRST (compare-exchange: only one of
            // the per-role DefaultChanged events wins), then raise. No lock is held.
            var pendingSwitch = Volatile.Read(ref _pendingDeviceOsd);
            if (pendingSwitch is not null && Environment.TickCount64 > pendingSwitch.DeadlineTickMs)
            {
                // Expired: the switch never landed. Discard without raising.
                Interlocked.CompareExchange(ref _pendingDeviceOsd, null, pendingSwitch);
                pendingSwitch = null;
            }
            if (pendingSwitch is not null
                && e.Device.Flow == AudioFlow.Render
                && string.Equals(pendingSwitch.TargetId, e.Device.Id, StringComparison.OrdinalIgnoreCase)
                && Interlocked.CompareExchange(ref _pendingDeviceOsd, null, pendingSwitch) == pendingSwitch)
            {
                ShowDeviceOsd(e.Device.Id, e.Device.FriendlyName);
            }

            // Not forced: PushControllerState already skips the send when the encoded state
            // (mute flags + active-device index) is unchanged, but a real default-device change
            // always does change the active-device index, so this still pushes (Bug 4 note).
            PushControllerState();
        }
    }

    /// <summary>Raises <see cref="VolumeChanged"/> with an <see cref="OsdKind.Mute"/>
    /// notification reflecting the target device's post-write mute flag and real level (the
    /// OSD shows "Muted", or the percentage on unmute).</summary>
    private void ShowMuteOsd(ButtonActionConfig actionConfig)
    {
        string? label;
        bool muted;
        float level;
        switch (actionConfig.Action)
        {
            case ButtonActionType.MuteCurrentOutput:
                label = Audio.GetDefaultDevice(AudioFlow.Render)?.FriendlyName ?? "Output";
                muted = ReadState(DeviceRef.DefaultOutput(), out level);
                break;
            case ButtonActionType.MuteSpecificDevice:
                var specificDevice = ActionFactory.Resolve(actionConfig.Device);
                if (specificDevice == null) return; // unresolvable device config; nothing to show
                label = actionConfig.Device?.FriendlyName ?? "Output";
                muted = ReadState(specificDevice.Value, out level);
                break;
            case ButtonActionType.MuteCurrentInput:
                label = "Microphone";
                muted = ReadState(DeviceRef.DefaultInput(), out level);
                break;
            default:
                return; // non-mute actions don't show the OSD
        }

        RaiseVolumeChanged(new VolumeChangeNotification(label, level, muted, OsdKind.Mute));
    }

    /// <summary>Raises <see cref="VolumeChanged"/> with an <see cref="OsdKind.Device"/>
    /// notification: the new default output's name plus its current level and mute flag
    /// (level 0, not muted when the state cannot be read).</summary>
    private void ShowDeviceOsd(string deviceId, string friendlyName)
    {
        var muted = ReadState(DeviceRef.ById(deviceId), out var level);
        var label = string.IsNullOrEmpty(friendlyName) ? "Output" : friendlyName;
        RaiseVolumeChanged(new VolumeChangeNotification(label, level, muted, OsdKind.Device));
    }

    /// <summary>Reads a device's level and mute flag; level 0 and not muted when unreadable.</summary>
    private bool ReadState(DeviceRef device, out float level)
    {
        if (Audio.TryGetDeviceState(device, out level, out var muted)) return muted;
        level = 0f;
        return false;
    }

    private void OnSliderVolumeChanged(object? sender, VolumeChangeNotification n) => RaiseVolumeChanged(n);

    private void OnSliderLevelChanged(object? sender, SliderLevelEventArgs e)
    {
        if (IsDisposed) return;
        SliderLevelChanged?.Invoke(this, e);
    }

    private void RaiseVolumeChanged(VolumeChangeNotification n)
    {
        if (IsDisposed) return;
        VolumeChanged?.Invoke(this, n);
    }

    /// <summary>
    /// Sends the controller-state frames (one 0x05 ButtonFeedback per button — never volume level;
    /// see <see cref="ButtonFeedback"/>) if they differ from what was last actually sent, or
    /// unconditionally when <paramref name="force"/> is true.
    /// </summary>
    /// <remarks>
    /// Bug 4: Core Audio's endpoint-volume callback fires for the engine's own writes too, so
    /// OnDeviceVolumeChanged previously called this unconditionally — up to ~50 times/sec while
    /// a Device-mapped slider is dragged, each one a synchronous blocking write
    /// (SerialConnection.Send/UdpConnection.Send) on the audio thread. Since the encoded state
    /// never includes volume level, a slider drag never actually changes it, so comparing against
    /// the last-pushed frames and skipping identical pushes eliminates the flood without missing
    /// any real mute/active-device change (see OnDeviceVolumeChanged/OnDevicesChangedForControllerState).
    /// </remarks>
    private void PushControllerState(bool force = false)
    {
        if (IsDisposed) return;

        // Read once: _connection can be replaced by StartConnection on another thread, and the
        // status check and the sends below must talk to the same transport.
        var connection = Volatile.Read(ref _connection);
        if (connection?.Status != ConnectionStatus.Connected) return;

        // BuildFrames only performs the read-only engine queries IAudioEngine documents as safe
        // from any thread (GetDefaultDevice/TryGetDeviceState) — no lock held here.
        var frames = ButtonFeedback.BuildFrames(Config.Config, ActivePresetId, Audio);

        lock (_pushStateLock)
        {
            if (!force && _lastPushedFrames != null && FramesEqual(_lastPushedFrames, frames))
                return;
            _lastPushedFrames = frames;
        }

        // Send happens outside the lock — Send() is a synchronous blocking transport write and
        // must never run while _pushStateLock is held.
        foreach (var frame in frames)
            connection.Send(frame);
    }

    private static bool FramesEqual(IReadOnlyList<byte[]> a, IReadOnlyList<byte[]> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!a[i].AsSpan().SequenceEqual(b[i])) return false;
        }
        return true;
    }

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        AppLog.Info("Connection configuration changed, restarting connection");
        // Handler is invoked only when connection config changes
        StartConnection();
    }

    /// <summary>
    /// Disposes the transport, SliderProcessor and pending timers and unsubscribes from the
    /// engine. Does NOT dispose the IAudioEngine: the head that created it owns it.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        Config.ConnectionConfigChanged -= OnConfigChanged;

        // Stop any pending post-connect re-pushes before tearing anything else down — Dispose()
        // on a System.Threading.Timer only guarantees no *new* callback queues after it returns;
        // one already queued on the thread pool can still run. That race is unavoidable without a
        // WaitHandle-based dispose we don't need here: the callback (PushControllerState) checks
        // IsDisposed, reads _connection once and tolerates null, and no-ops unless Connected.
        lock (_postConnectLock)
        {
            DisposePostConnectTimers();
        }

        // Take ownership of the current transport (see _connection): a concurrent restart that
        // already took it retires it itself, and one that publishes a new transport after this
        // point sees IsDisposed and retires that one itself.
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection != null)
            RetireTransport(connection); // detached first: nothing it raises while shutting down is reported

        if (_sliderProcessor is not null)
        {
            _sliderProcessor.VolumeChanged -= OnSliderVolumeChanged;
            _sliderProcessor.SliderLevelChanged -= OnSliderLevelChanged;
            _sliderProcessor.Dispose();
        }

        if (_buttonPressDetector is not null)
            _buttonPressDetector.PressDetected -= OnButtonPressDetected;

        Audio.DeviceVolumeChanged -= OnDeviceVolumeChanged;
        Audio.DevicesChanged -= OnDevicesChangedForControllerState;
    }
}
