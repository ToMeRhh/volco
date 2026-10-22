using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Devices;
using Volco.Core.Diagnostics;
using Volco.Core.Input;
using Volco.Core.Input.Actions;
using Volco.Core.Media;
using Volco.Core.Programs;
using Volco.Core.Scripts;

namespace Volco.Core.Runtime;

/// <summary>
/// Everything Volco does that is not a window: transport construction and restart, inbound
/// message routing to <see cref="SliderProcessor"/> / <see cref="ButtonPressDetector"/>,
/// <see cref="ActionFactory"/> dispatch, the pending-mute-OSD correlation, and the controller
/// session (<see cref="ControllerLink"/>: Hello, keepalive, LED and screen pushes). Headless: a UI
/// head (the Avalonia head, Volco.App) constructs one, calls <see cref="Start"/>, and renders
/// what the events publish.
/// </summary>
/// <remarks>
/// Threading: every event on this class may be raised on ANY thread - the audio thread
/// (engine callbacks), a transport's receive/timer threads, the ConfigManager debounce-timer
/// thread, the SliderProcessor coalescing timer, the ButtonPressDetector timers, or the
/// controller link's keepalive timer. Subscribers marshal to their UI thread themselves.
/// </remarks>
public sealed class VolcoRuntime : IDisposable, IModeController, IConfigSessionLink, IButtonFeedback
{
    /// <summary>The live mode's id as last set; null until the first set. Read through
    /// <see cref="ActiveModeId"/>, which falls back to the first mode when this one is gone
    /// (deleted in the settings UI) or was never set.</summary>
    private string? _activeModeId;

    private readonly bool _audioReady;
    private readonly Func<ConnectionConfig, IControllerConnection?> _connectionFactory;
    private readonly VolcoRuntimeOptions _options;
    private readonly ControllerLink _link;

    /// <summary>
    /// The current transport. Ownership handoff: whoever removes a transport from this field
    /// with an Interlocked operation (StartConnection when replacing it, or Dispose) is the one
    /// and only party that detaches and disposes it. It is only ever written with
    /// Interlocked.Exchange / CompareExchange, so a restart on the ConfigManager debounce thread
    /// and a concurrent Dispose can never both dispose the same transport, and a transport a
    /// racing restart publishes is always found by exactly one of them. Readers
    /// (TransportStatus, the sender filter) read it once and tolerate
    /// null.
    /// </summary>
    private IControllerConnection? _connection;

    /// <summary>The current transport when it is USB serial, else null: a press from it may run
    /// programs and scripts (custom-actions spec §7). Written with the transport's publish.</summary>
    private IControllerConnection? _serialTransport;

    /// <summary>Per button input: whether its last press frame came over USB serial. Read when the
    /// detector fires the gesture (after the release, maybe after a double-press wait).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> _pressOverUsb = new();

    /// <summary>Per button input: the feedback for a press just made (custom-actions spec §11),
    /// moved on every <see cref="ControllerDisplay.FeedbackStep"/> by its own timer.</summary>
    private readonly Dictionary<int, FeedbackEntry> _feedback = new();
    private readonly object _feedbackGate = new();

    private sealed class FeedbackEntry
    {
        public required PressFeedback Feedback;
        public required ITimer Timer;
    }

    private readonly IMediaSessions? _media;
    private readonly ProgramOpener? _programs;
    private readonly ScriptRunner? _scripts;
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

    /// <summary>0/1, see <see cref="IsUserDisconnected"/>. Written only with Interlocked.</summary>
    private int _userDisconnected;

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

    /// <param name="config">Already loaded by the head.</param>
    /// <param name="audio">The platform engine, already started. Owned by the head, not by this
    /// runtime.</param>
    /// <param name="audioReady">What the platform engine's Start() returned. False → no config
    /// migration, AudioEngineFailed = true; everything else still starts.</param>
    /// <param name="connectionFactory">Null → the real Serial/Udp/WebSocket switch. Tests inject fakes.</param>
    /// <param name="options">Null → <see cref="VolcoRuntimeOptions.Default"/>.</param>
    /// <param name="custom">Media sessions, program launcher and process starter; null or a null
    /// member → those actions are not available.</param>
    public VolcoRuntime(ConfigManager config, IAudioEngine audio, bool audioReady,
                        Func<ConnectionConfig, IControllerConnection?>? connectionFactory = null,
                        VolcoRuntimeOptions? options = null, CustomActionServices? custom = null)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        Audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _audioReady = audioReady;
        _connectionFactory = connectionFactory
                             ?? new Func<ConnectionConfig, IControllerConnection?>(CreateDefaultConnection);
        _options = options ?? VolcoRuntimeOptions.Default;
        _media = custom?.Media;
        _programs = custom?.Launcher is { } launcher ? new ProgramOpener(launcher, _options.Time) : null;
        _scripts = custom?.Processes is { } processes ? new ScriptRunner(processes, _options.Time) : null;
        ActionStatus = new ActionStatus(_media, _programs, _scripts);
        // In test mode the same delegate shows the test state and ignores the announce step
        // (spec §8.5), so no mode blink or flash appears during a test.
        _link = new ControllerLink(
            step => InTestMode ? TestDisplays() : ControllerDisplay.Compute(Config.Config, ActiveModeId, Audio, step, ActionStatus, FeedbackSnapshot()),
            ControllerDisplay.AnnounceSteps, _options.Link);
        _link.StateChanged += (_, state) =>
        {
            AppLog.Info($"Controller state: {state}");
            WatchForSilence(state);
            if (!IsDisposed) ControllerStateChanged?.Invoke(this, state);
            UpdateLink();
        };
        _link.HelloReceived += OnHello;
        _link.Announced += (_, _) => RaiseFeedbackChanged(-1);
        ControllerConfig = new ControllerConfigService(Config, this, _options.Time, () => _options.Firmware.BundledVersion);
        ControllerConfig.OperationChanged += (_, _) => UpdateLink();
    }

    public IAudioEngine Audio { get; }

    /// <summary>The live state of media, program and script actions (for the settings previews).</summary>
    public IActionStatus ActionStatus { get; }

    /// <summary>The media sessions, or null where this platform has none (the media app picker).</summary>
    public IMediaSessions? Media => _media;

    public bool ProgramsAvailable => _programs is not null;
    public bool ScriptsAvailable => _scripts is not null;

    private ActionServices Services => new(Audio, this, _media, _programs, _scripts);

    // ── Test mode (spec §8.5) ────────────────────────────────────────────────────────────────

    private readonly object _testGate = new();
    private int _testGeneration;                 // guarded by _testGate; 0 = not testing
    private int[] _testPresses = Array.Empty<int>();   // guarded by _testGate; presses per button this test

    /// <summary>A hardware test is running: Slider and Button messages raise only <see cref="RawInput"/>
    /// and reach neither audio nor actions.</summary>
    public bool InTestMode { get { lock (_testGate) return _testGeneration != 0; } }

    /// <summary>Test mode started (true) or ended (false), also when the runtime ended it itself
    /// (Detach, connection replaced, dispose). Any thread.</summary>
    public event EventHandler<bool>? InTestModeChanged;

    /// <summary>Every Slider and Button message, raw (in a test, with its press count). Any thread
    /// (the transport's).</summary>
    public event EventHandler<RawInputEventArgs>? RawInput;

    private int _nextTestGeneration;

    /// <summary>Starts a hardware test, or joins the one running. Disposing the handle ends the
    /// session it belongs to (never a later one); it is idempotent.</summary>
    public IDisposable EnterTestMode()
    {
        int generation;
        bool started = false;
        lock (_testGate)
        {
            if (IsDisposed) return new TestHandle(this, -1);
            if (_testGeneration == 0)
            {
                _testGeneration = ++_nextTestGeneration;
                _testPresses = Array.Empty<int>();
                started = true;
            }
            generation = _testGeneration;
        }
        if (started)
        {
            AppLog.Info("Test mode on");
            if (ClearFeedback()) RaiseFeedbackChanged(-1);
            _buttonPressDetector?.Reset();   // a gesture started before the test can't fire during it
            _sliderProcessor?.DropPending();  // nor a slider value queued just before it
            _link.PushAll();
            InTestModeChanged?.Invoke(this, true);
        }
        return new TestHandle(this, generation);
    }

    /// <summary>A button was pressed during the test: its indicator moves on (spec §8.5). Returns
    /// the button's presses so far in this test, 0 when no test is running.</summary>
    private int CountTestPress(int button)
    {
        int presses;
        lock (_testGate)
        {
            if (_testGeneration == 0 || button < 0) return 0;
            if (_testPresses.Length <= button) Array.Resize(ref _testPresses, button + 1);
            presses = ++_testPresses[button];
        }
        _link.Push();
        return presses;
    }

    /// <summary>A click on a button's light in the hardware test (hardware-page spec §8): its test
    /// indicator moves on exactly as one press of <paramref name="button"/> (the 0-based input)
    /// would and is pushed; no action runs and no audio changes. Returns the button's presses so
    /// far in this test, 0 when no test is running.</summary>
    public int AdvanceTestIndicator(int button) => IsDisposed ? 0 : CountTestPress(button);

    private int TestPresses(int button)
    {
        lock (_testGate) return button >= 0 && button < _testPresses.Length ? _testPresses[button] : 0;
    }

    /// <summary>Ends the test (only <paramref name="generation"/>'s session; -1 for any).</summary>
    private void ExitTestMode(int generation)
    {
        lock (_testGate)
        {
            if (_testGeneration == 0 || (generation != -1 && generation != _testGeneration)) return;
            _testGeneration = 0;
            _testPresses = Array.Empty<int>();
        }
        AppLog.Info("Test mode off");
        _buttonPressDetector?.Reset();       // a button held across the exit fires nothing
        _link.PushAll();                     // the real indicator state
        if (!IsDisposed) InTestModeChanged?.Invoke(this, false);
    }

    private sealed class TestHandle(VolcoRuntime runtime, int generation) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (generation < 0 || Interlocked.Exchange(ref _disposed, 1) != 0) return;
            runtime.ExitTestMode(generation);
        }
    }

    // ── Slider calibration (slider-calibration spec) ─────────────────────────────────────────

    private readonly object _calibrationGate = new();
    private readonly Dictionary<int, SliderCalibrationState> _calibrations = new();   // guarded by _calibrationGate
    private readonly Dictionary<int, SliderHold> _holds = new();                      // guarded by _calibrationGate
    private int _nextHoldGeneration;                                                  // guarded by _calibrationGate

    /// <summary>One slider's input hold. <see cref="SawSweep"/>: a report since it began said the
    /// slider was sweeping, so the next one that says it isn't is the sweep's end.</summary>
    private sealed class SliderHold(int generation)
    {
        public int Generation { get; } = generation;
        public bool SawSweep { get; set; }
    }

    /// <summary>A slider's calibration changed (its 0-based input and its new state): a Calibration
    /// report arrived from the current connection, or, with a null state, a Hello made the last one
    /// unknown until the slider's answer. Any thread (the transport's).</summary>
    public event EventHandler<(int Slider, SliderCalibrationState? State)>? CalibrationChanged;

    /// <summary>The controller's last report for a slider (0-based input), or null before one.
    /// Every slider's state is forgotten and queried again after each Hello.</summary>
    public SliderCalibrationState? GetCalibration(int sliderIndex)
    {
        lock (_calibrationGate) return _calibrations.TryGetValue(sliderIndex, out var state) ? state : null;
    }

    /// <summary>Sends a Calibrate for a slider (0-based input). False when no connection is attached.</summary>
    public bool SendCalibrate(int sliderIndex, CalibrateAction action)
    {
        if (IsDisposed || sliderIndex is < 0 or > byte.MaxValue) return false;
        return _link.SendRaw(ProtocolEncoder.EncodeCalibrate(sliderIndex, action));
    }

    /// <summary>Keeps a slider's input (0-based) away from audio during a sweep: its Slider messages
    /// still raise <see cref="RawInput"/> but reach no slider processor. Released by disposing the
    /// handle (idempotent; never a later hold), by the report that ends the sweep (Sweeping off after
    /// one that had it on), by a connection replacement, Detach or the transport closing, and on
    /// dispose. On a replacement and on dispose the runtime also cancels the sweep.</summary>
    public IDisposable HoldSlider(int sliderIndex)
    {
        int generation;
        lock (_calibrationGate)
        {
            if (IsDisposed) return new SliderHoldHandle(this, sliderIndex, -1);
            generation = ++_nextHoldGeneration;
            _holds[sliderIndex] = new SliderHold(generation);
        }
        AppLog.Info($"Slider {sliderIndex + 1} held for calibration");
        return new SliderHoldHandle(this, sliderIndex, generation);
    }

    /// <summary>A hold keeps this slider's input (0-based) away from audio.</summary>
    public bool IsSliderHeld(int sliderIndex)
    {
        lock (_calibrationGate) return _holds.ContainsKey(sliderIndex);
    }

    private void ReleaseSlider(int sliderIndex, int generation)
    {
        lock (_calibrationGate)
        {
            if (!_holds.TryGetValue(sliderIndex, out var hold) || hold.Generation != generation) return;
            _holds.Remove(sliderIndex);
        }
        AppLog.Info($"Slider {sliderIndex + 1} released");
    }

    /// <summary>Ends every hold; with <paramref name="cancelSweeps"/>, first tells the controller to
    /// cancel each held slider's sweep (best effort: the controller also cancels when the host is lost).</summary>
    private void ReleaseAllSliders(bool cancelSweeps)
    {
        int[] held;
        lock (_calibrationGate)
        {
            held = _holds.Keys.ToArray();
            _holds.Clear();
        }
        foreach (var slider in held)
        {
            if (cancelSweeps) _link.SendRaw(ProtocolEncoder.EncodeCalibrate(slider, CalibrateAction.Cancel));
            AppLog.Info($"Slider {slider + 1} released{(cancelSweeps ? ", its sweep cancelled" : "")}");
        }
    }

    private void OnCalibration(CalibrationMessage message)
    {
        var state = SliderCalibrationState.From(message);
        bool released = false, orphan = false;
        lock (_calibrationGate)
        {
            _calibrations[message.SliderIndex] = state;
            // A sweep no hold is waiting for (this app restarted or its dialog is gone): cancel it, so
            // the slider's input is not stuck learning. A dialog takes its hold before it sends Start.
            if (!_holds.TryGetValue(message.SliderIndex, out var hold)) orphan = state.Sweeping;
            else
            {
                if (state.Sweeping) hold.SawSweep = true;
                else if (hold.SawSweep)
                {
                    _holds.Remove(message.SliderIndex);
                    released = true;
                }
            }
        }
        AppLog.Debug($"Calibration: index={message.SliderIndex}, learned={state.Learned}, sweeping={state.Sweeping}, " +
                     $"auto={state.AutoCalibrate}, ends {state.Low}-{state.High}");
        if (released) AppLog.Info($"Slider {message.SliderIndex + 1} released: its sweep ended");
        if (orphan)
        {
            AppLog.Info($"Slider {message.SliderIndex + 1} is sweeping with no calibration open: cancelling it");
            _link.SendRaw(ProtocolEncoder.EncodeCalibrate(message.SliderIndex, CalibrateAction.Cancel));
        }
        CalibrationChanged?.Invoke(this, (message.SliderIndex, state));
    }

    /// <summary>After a Hello this app speaks: forgets every slider's calibration (the controller may
    /// have rebooted with another config) and asks for each one again.</summary>
    private void QueryCalibrations(int sliderCount)
    {
        int[] cleared;
        lock (_calibrationGate)
        {
            cleared = _calibrations.Keys.Order().ToArray();
            _calibrations.Clear();
        }
        foreach (var slider in cleared) CalibrationChanged?.Invoke(this, (slider, null));
        for (int i = 0; i < sliderCount; i++)
            _link.SendRaw(ProtocolEncoder.EncodeCalibrate(i, CalibrateAction.Query));
    }

    private sealed class SliderHoldHandle(VolcoRuntime runtime, int slider, int generation) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (generation < 0 || Interlocked.Exchange(ref _disposed, 1) != 0) return;
            runtime.ReleaseSlider(slider, generation);
        }
    }

    /// <summary>The test state of every button, from its presses so far (<see cref="TestMode"/>).</summary>
    private IReadOnlyList<ButtonDisplay?> TestDisplays()
    {
        var kinds = Config.Config.Controller?.Buttons ?? new List<IndicatorKind>();
        int[] presses;
        lock (_testGate) presses = (int[])_testPresses.Clone();
        var result = new ButtonDisplay?[kinds.Count];
        for (int i = 0; i < kinds.Count; i++)
        {
            var n = i < presses.Length ? presses[i] : 0;
            result[i] = kinds[i] switch
            {
                IndicatorKind.Led or IndicatorKind.LedPair => new LedDisplay(TestMode.Indicator(kinds[i], n) switch
                {
                    TestIndicator.On or TestIndicator.A => (byte)1,
                    TestIndicator.B => (byte)2,
                    _ => (byte)0,
                }),
                IndicatorKind.Screen => new ScreenDisplay(TestMode.Screen(i, n)),
                _ => null,
            };
        }
        return result;
    }

    /// <summary>The controller config session: reads, saves, the mirror and the banner (spec §8.2).</summary>
    public ControllerConfigService ControllerConfig { get; }

    bool IConfigSessionLink.SendRaw(byte[] burst) => !IsDisposed && _link.SendRaw(burst);

    ConnectionType IConfigSessionLink.ConnectionType => Config.Config.Connection.Type;
    public ConfigManager Config { get; }
    public bool AudioEngineFailed => !_audioReady;

    /// <summary>The current transport's status; Closed when there is no transport. A port or socket
    /// fact only: the UI reads <see cref="LinkStatus"/>.</summary>
    public TransportStatus TransportStatus => Volatile.Read(ref _connection)?.Status ?? TransportStatus.Closed;

    /// <summary>The address the controller talks from, when the transport knows one (UDP).</summary>
    public string? PeerAddress => Volatile.Read(ref _connection)?.PeerAddress;

    /// <summary>Where the controller session stands (spec §5.5).</summary>
    public ControllerState ControllerState => _link.State;

    /// <summary>Every Hello from the current connection, version-only ones included, after the
    /// link and the config session have it (the firmware service waits on it). Any thread.</summary>
    public event EventHandler<HelloMessage>? HelloReceived;

    /// <summary>The controller session's state changed. Any thread.</summary>
    public event EventHandler<ControllerState>? ControllerStateChanged;

    /// <summary>A Hello reported hardware different from the stored <see cref="AppConfig.Controller"/>
    /// (which has already been updated). Any thread.</summary>
    public event EventHandler? ControllerInfoChanged;

    /// <summary>
    /// Fix D: the most recently processed volume [0,1] for a slider index, or null before
    /// <see cref="Start"/>, after <see cref="Dispose"/>, or when that slider has never reported a
    /// value yet (the firmware only sends a slider's value when it moves, so this can genuinely
    /// be unknown for a long time after app start). Additive: lets a UI seed its live-level
    /// display from state the runtime already holds instead of waiting on
    /// <see cref="SliderLevelChanged"/> alone, which a window hidden since before a slider last
    /// moved would otherwise never receive.
    /// </summary>
    /// <summary>Sets what slider <paramref name="sliderIndex"/> controls now to the slider's last
    /// reported position (its target changed in the settings UI). No OSD.</summary>
    public void ReapplySlider(int sliderIndex)
    {
        if (!IsDisposed) _sliderProcessor?.Reapply(sliderIndex);
    }

    public float? GetSliderLevel(int sliderIndex)
        => IsDisposed ? null : _sliderProcessor?.GetLastLevel(sliderIndex);

    // All events may be raised on ANY thread (audio thread, transport threads, timer threads).
    // Subscribers marshal to their UI thread themselves.

    /// <summary>Slider volume OSD (re-raised from SliderProcessor) and mute OSD (ShowMuteOsd).</summary>
    public event EventHandler<VolumeChangeNotification>? VolumeChanged;

    /// <summary>Re-raised from <see cref="SliderProcessor.SliderLevelChanged"/>.</summary>
    public event EventHandler<SliderLevelEventArgs>? SliderLevelChanged;

    /// <summary>
    /// Status of whichever transport is current. Survives transport restarts:
    /// the runtime attaches its handler to each transport it creates and detaches it from the old
    /// one on restart, so a subscriber never has to re-subscribe to a new IControllerConnection.
    /// </summary>
    public event EventHandler<TransportStatus>? TransportStatusChanged;

    // ── The link state (what the UI calls connected) ─────────────────────────────────────────

    private readonly object _linkGate = new();
    private readonly List<Interruption> _interruptions = new();   // guarded by _linkGate
    private LinkStatus? _lastLink;                                  // guarded by _linkGate

    /// <summary>What the user is told: connected only once the controller answered with this
    /// app's protocol version; an expected interruption is a wait, not a failure.</summary>
    public LinkStatus LinkStatus
    {
        get { lock (_linkGate) return ComputeLinkLocked(); }
    }

    /// <summary><see cref="LinkStatus"/> changed. Any thread; a handler that posts to its UI thread
    /// reads <see cref="LinkStatus"/> there rather than trusting the argument's order.</summary>
    public event EventHandler<LinkStatus>? LinkStatusChanged;

    /// <summary>
    /// Volco is about to cause a gap in the link itself (a firmware operation, a connection it just
    /// started): until the handle is disposed, or <paramref name="limit"/> runs out, the link
    /// reports <see cref="LinkState.Waiting"/> with <paramref name="activity"/> instead of what the
    /// transport says, and the silence timer stays quiet, so the tray never turns red for it. A
    /// controller that answers is still Connected (or Incompatible) meanwhile.
    /// </summary>
    public IDisposable ExpectInterruption(LinkActivity activity, TimeSpan? limit = null)
    {
        var handle = new Interruption(this, activity);
        lock (_linkGate) _interruptions.Add(handle);
        if (limit is { } l) handle.Expire(_options.Time.CreateTimer(_ => handle.Dispose(), null, l, Timeout.InfiniteTimeSpan));
        UpdateLink();
        return handle;
    }

    /// <summary>An expected interruption is held.</summary>
    private bool IsInterrupted
    {
        get { lock (_linkGate) return _interruptions.Count > 0; }
    }

    private void EndInterruption(Interruption handle)
    {
        bool last;
        lock (_linkGate)
        {
            if (!_interruptions.Remove(handle)) return;
            last = _interruptions.Count == 0;
        }
        // The silence the interruption held back counts now, if the port is still quiet.
        if (last && Interlocked.Exchange(ref _silenceDeferred, 0) == 1) CheckSilence();
        UpdateLink();
    }

    private sealed class Interruption(VolcoRuntime runtime, LinkActivity activity) : IDisposable
    {
        private int _disposed;
        private ITimer? _timer;

        public LinkActivity Activity => activity;

        /// <summary>The timer that disposes this handle at its limit.</summary>
        public void Expire(ITimer timer)
        {
            Interlocked.Exchange(ref _timer, timer)?.Dispose();
            if (Volatile.Read(ref _disposed) != 0) Interlocked.Exchange(ref _timer, null)?.Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Interlocked.Exchange(ref _timer, null)?.Dispose();
            runtime.EndInterruption(this);
        }
    }

    private LinkStatus ComputeLinkLocked() => LinkStateLogic.Compute(new LinkInputs(
        IsUserDisconnected, TransportStatus, _link.State, ControllerSilent, HeldActivityLocked(),
        ControllerConfig.Operation == ConfigOperation.Writing));

    /// <summary>The newest interruption's activity, a firmware operation's before a connect grace
    /// (the grace of the reconnect that ends a flash must not turn "Updating firmware…" into
    /// "Connecting…").</summary>
    private LinkActivity HeldActivityLocked()
    {
        var activity = LinkActivity.None;
        foreach (var i in _interruptions)
            if (activity is LinkActivity.None or LinkActivity.Connecting || i.Activity != LinkActivity.Connecting)
                activity = i.Activity;
        return activity;
    }

    /// <summary>Recomputes the link state and raises <see cref="LinkStatusChanged"/> when it
    /// changed. Called after every input changes.</summary>
    private void UpdateLink()
    {
        if (IsDisposed) return;
        LinkStatus status;
        lock (_linkGate)
        {
            status = ComputeLinkLocked();
            if (status == _lastLink) return;
            _lastLink = status;
        }
        AppLog.Info(status.Activity == LinkActivity.None ? $"Link: {status.State}" : $"Link: {status.State} ({status.Activity})");
        LinkStatusChanged?.Invoke(this, status);
    }

    /// <summary>Every <see cref="IAudioEngine.DevicesChanged"/>, of every kind, forwarded.</summary>
    public event EventHandler<DevicesChangedEventArgs>? DevicesChanged;

    /// <summary>The live mode changed (argument: its id), from a button or from <see cref="SetActiveMode"/>.</summary>
    public event EventHandler<string>? ActiveModeChanged;

    /// <summary>A button on the controller was pressed down (argument: its hardware input),
    /// whether or not it is configured. Lets a UI show which card is which physical button.</summary>
    public event EventHandler<int>? ButtonActivity;

    /// <inheritdoc/>
    public string ActiveModeId
    {
        get
        {
            var modes = Config.Config.Modes;
            var id = Volatile.Read(ref _activeModeId);
            if (id != null && modes.Exists(p => p.Id == id)) return id;
            return modes.Count > 0 ? modes[0].Id : AppConfig.DefaultModeId;
        }
    }

    /// <inheritdoc/>
    public void SetActiveMode(string modeId) => SetActiveMode(modeId, transition: null);

    /// <inheritdoc/>
    public void NextMode()
    {
        var modes = Config.Config.Modes;
        if (modes.Count == 0) return;
        var index = modes.FindIndex(p => p.Id == ActiveModeId);
        // Next always slides left, including the wrap from the last mode to the first (spec §2.3).
        SetActiveMode(modes[(index + 1) % modes.Count].Id, ScreenTransition.SlideLeft);
    }

    private void SetActiveMode(string modeId, ScreenTransition? transition)
    {
        if (IsDisposed) return;
        var modes = Config.Config.Modes;
        if (!modes.Exists(p => p.Id == modeId)) return;
        var stored = Volatile.Read(ref _activeModeId);
        var previous = ActiveModeId;
        Volatile.Write(ref _activeModeId, modeId);
        if (previous == modeId)
        {
            // The live mode was deleted and ActiveModeId already fell back to this one (also
            // when none was ever set explicitly, so the deleted one was itself the fallback):
            // nothing slides, but the controller must catch up now (spec §2.3).
            if (stored is null || !modes.Exists(p => p.Id == stored)) _link.PushAll();
            return;
        }

        AppLog.Info($"Active mode: {modeId}");
        if (ClearFeedback()) RaiseFeedbackChanged(-1);   // the other mode's actions: nothing pressed there yet
        WatchPrograms();
        ActiveModeChanged?.Invoke(this, modeId);

        var from = modes.FindIndex(p => p.Id == previous);
        var to = modes.FindIndex(p => p.Id == modeId);
        var slide = transition ?? (to > from ? ScreenTransition.SlideLeft : ScreenTransition.SlideRight);
        _link.PushModeChange(slide, _options.SynchronizedModeTransitions);
    }

    /// <summary>Drops the current transport and starts a fresh one from the current connection
    /// config (the settings UI's Reconnect button). Also ends a <see cref="Disconnect"/>.</summary>
    public void Reconnect()
    {
        if (IsDisposed || !_started) return;
        AppLog.Info("Reconnect requested");
        ExpectConnect();
        SetUserDisconnected(false);
        StartConnection();
    }

    /// <summary>
    /// True after <see cref="Disconnect"/> until <see cref="Connect"/> or <see cref="Reconnect"/>:
    /// there is no transport, and connection-config changes are saved but start none. Not
    /// persisted; every launch connects.
    /// </summary>
    public bool IsUserDisconnected => Volatile.Read(ref _userDisconnected) != 0;

    /// <summary><see cref="IsUserDisconnected"/> changed (argument: its new value). Any thread.</summary>
    public event EventHandler<bool>? UserDisconnectedChanged;

    /// <summary>Drops the current transport and starts none until <see cref="Connect"/> (the
    /// settings UI's and the tray's Disconnect).</summary>
    public void Disconnect()
    {
        if (IsDisposed || !_started) return;
        AppLog.Info("Disconnect requested");
        // Flag first, then take the transport: a StartConnection racing this one either sees the
        // flag after publishing and retires its own transport, or published before the flag was
        // set and so is taken out of _connection below (both are full fences, as with Dispose).
        if (!SetUserDisconnected(true)) return;
        StopConnection();
    }

    /// <summary>Ends a <see cref="Disconnect"/>: starts a transport from the current connection
    /// config. Does nothing while connected normally.</summary>
    public void Connect()
    {
        if (IsDisposed || !_started || !IsUserDisconnected) return;
        ExpectConnect();   // before the flag clears, so "Disconnected" turns straight into "Connecting…"
        if (!SetUserDisconnected(false)) return;
        AppLog.Info("Connect requested");
        StartConnection();
    }

    /// <summary>Sets the flag and raises the event when it changed; returns whether it did.</summary>
    private bool SetUserDisconnected(bool value)
    {
        var next = value ? 1 : 0;
        if (Interlocked.Exchange(ref _userDisconnected, next) == next) return false;
        if (!IsDisposed) UserDisconnectedChanged?.Invoke(this, value);
        UpdateLink();
        return true;
    }

    /// <summary>Runs one action now, as a button press would (the settings UI's Test button).
    /// Mode actions change the live mode.</summary>
    public void Execute(ButtonActionConfig action) => Execute(action, null);

    /// <summary>As <see cref="Execute(ButtonActionConfig)"/>, for a button and gesture (a script's
    /// environment). The settings UI is local, so programs and scripts run whatever the transport.</summary>
    public void Execute(ButtonActionConfig action, ActionOrigin? origin)
    {
        if (IsDisposed) return;
        if (ActionValidator.Validate(action, ActionValidationContext.Structural(Config.Config)).Count > 0) return;
        // The button whose binding holds this action shows its feedback, as for a press: in its
        // preview whatever the mode, and on the controller when that mode is live.
        var binding = origin is { Button: var b } && b >= 0 && b < Config.Config.Buttons.Count
            ? Config.Config.Buttons[b].Actions.Values.FirstOrDefault(x => x.All().Any(a => ReferenceEquals(a, action)))
            : null;
        if (binding is not null)
            origin = origin! with { ReportError = why => StartFeedback(origin!.Button, binding, action, why) };
        var created = ActionFactory.Create(action, Services, origin);
        if (binding is not null)
        {
            if (created is null && action.Action != ButtonActionType.None)
                StartFeedback(origin!.Button, binding, action, ControllerDisplay.NotAvailable);
            else if (created is not null)
                StartFeedback(origin!.Button, binding, action, error: null);
        }
        created?.Execute();
    }

    /// <summary>The invalid actions already logged, so a held-down or repeated press logs once.</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ButtonActionConfig, object> _loggedInvalid = new();

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
        if (_media is not null) _media.Changed += OnActionStateChanged;
        if (_programs is not null) _programs.Changed += OnActionStateChanged;
        if (_scripts is not null) _scripts.Changed += OnActionStateChanged;
        WatchPrograms();

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

        // A device renamed in Windows while Volco was closed: the stored names follow.
        if (_audioReady && RefreshStoredDeviceNames()) Config.Save();

        Config.ConnectionConfigChanged += OnConfigChanged;
        Config.ConfigChanged += OnSettingsChanged;

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
    /// <see cref="OnConfigChanged"/> (Bug 1). Does nothing while <see cref="IsUserDisconnected"/>.
    /// </summary>
    private void StartConnection()
    {
        // Spec §8.6: while a firmware probe or flash holds the port, nothing reopens it; nor after
        // the user's Disconnect.
        if (IsDisposed || IsSuspended || IsUserDisconnected) return;

        // Before the old transport goes, so the link never reports the gap between the two.
        ExpectConnect();
        StopConnection();

        var config = Config.Config.Connection;
        AppLog.Info($"Starting connection: {config.Type}");
        var connection = _connectionFactory(config);

        if (connection == null) return;

        connection.MessageReceived += OnMessageReceived;
        connection.StatusChanged += OnTransportStatusChanged;
        // Before the publish: a frame from the new transport is never judged against the old kind.
        Volatile.Write(ref _serialTransport, config.Type == ConnectionType.Serial ? connection : null);

        // Publish. Anything displaced is a transport a concurrent StartConnection published in
        // the meantime; by the ownership rule, removing it makes it ours to retire.
        var displaced = Interlocked.Exchange(ref _connection, connection);
        if (displaced != null)
        {
            ReleaseAllSliders(cancelSweeps: true);   // while the link still sends to the old transport
            RetireTransport(displaced);
            _link.Detach();
            ControllerConfig.OnConnectionReplaced();
            ExitTestMode(-1);
        }
        // After the replacement, which clears it; only while it is still the published one.
        ControllerConfig.OnConnectionStarted(connection, () => Volatile.Read(ref _connection));

        // Dispose() or SuspendConnection() may have started after the check at the top. Both the
        // publish above and their flag writes are full fences, so if this read still sees neither,
        // any that starts later is guaranteed to find the transport in _connection and retire it
        // itself. If it sees one, exactly one of us gets it out of the field: retire it only if
        // CompareExchange says it is still there, and never Connect() it.
        // Every transport (Serial, UDP, WebSocket) guards Connect() with its own disposed flag
        // under its own lock, so a Connect() that loses that race is a no-op and binds nothing -
        // no port or listener can outlive the Dispose(). No lock is needed here, and none is held
        // across Connect()/Dispose(), both of which raise StatusChanged synchronously into
        // subscribers (a deadlock risk for a UI that marshals synchronously).
        // The same holds for a Disconnect() or a suspension racing this restart.
        if (IsDisposed || IsSuspended || IsUserDisconnected)
        {
            if (Interlocked.CompareExchange(ref _connection, null, connection) == connection)
                RetireTransport(connection);
            return;
        }

        connection.Connect();
        UpdateLink();
    }

    /// <summary>A transport Volco is starting isn't a failure until it has had a moment to find the
    /// controller: no red tray for the first seconds of a launch, a Connect, a Reconnect or a port
    /// change. The handle ends itself after <see cref="VolcoRuntimeOptions.ConnectGrace"/>.</summary>
    private void ExpectConnect()
    {
        if (_options.ConnectGrace > TimeSpan.Zero) ExpectInterruption(LinkActivity.Connecting, _options.ConnectGrace);
    }

    /// <summary>Takes the current transport out of <see cref="_connection"/>, if any, and retires
    /// it as a replacement does: subscribers are told it is Disconnected, the link detaches, a
    /// config operation fails and test mode ends.</summary>
    private void StopConnection()
    {
        // Take ownership of the old transport (see _connection): if Dispose() got to it first,
        // this returns null and Dispose() retires it instead.
        var old = Interlocked.Exchange(ref _connection, null);
        if (old != null)
        {
            // Detached before Dispose(): the old transport can still raise
            // events from its background threads, and those must never be reported as the
            // status of the connection that replaces it. The Disconnected a real transport
            // raises from its own Dispose() therefore no longer reaches subscribers by itself,
            // so report it here, once, if they were last told something else - otherwise a
            // settings page would keep showing the old transport's "Connected" while e.g. a new
            // WebSocket listener sits Disconnected and raises nothing.
            var lastStatus = old.Status;
            ReleaseAllSliders(cancelSweeps: true);   // while the link still sends to the old transport
            RetireTransport(old);
            _link.Detach(); // its Disconnected no longer reaches OnTransportStatusChanged
            ControllerConfig.OnConnectionReplaced();
            ExitTestMode(-1);
            if (lastStatus != TransportStatus.Closed && !IsDisposed)
                TransportStatusChanged?.Invoke(this, TransportStatus.Closed);
        }
        UpdateLink();
    }

    // ── A silent controller ──────────────────────────────────────────────────────────────────

    private readonly object _silenceGate = new();
    private ITimer? _silenceTimer;   // guarded by _silenceGate
    private int _silent;
    /// <summary>1 when the silence timer ran out during an expected interruption: the end of the
    /// interruption checks again.</summary>
    private int _silenceDeferred;

    /// <summary>The USB port has been open for <see cref="VolcoRuntimeOptions.SilentAfter"/> with
    /// no Hello: whatever is on it isn't answering as a Volco controller.</summary>
    public bool ControllerSilent => Volatile.Read(ref _silent) != 0;

    /// <summary><see cref="ControllerSilent"/> changed. Any thread.</summary>
    public event EventHandler<bool>? ControllerSilentChanged;

    private void WatchForSilence(ControllerState state)
    {
        lock (_silenceGate)
        {
            _silenceTimer?.Dispose();
            _silenceTimer = null;
            if (state == ControllerState.AwaitingHello && !IsDisposed)
                _silenceTimer = _options.Time.CreateTimer(_ =>
                {
                    // Not during an expected interruption (a firmware operation waits for the Hello
                    // itself): its end checks again.
                    if (IsInterrupted) Volatile.Write(ref _silenceDeferred, 1);
                    else CheckSilence();
                }, null, _options.SilentAfter, Timeout.InfiniteTimeSpan);
        }
        if (state != ControllerState.AwaitingHello)
        {
            Volatile.Write(ref _silenceDeferred, 0);
            SetSilent(false);
        }
    }

    private void CheckSilence()
    {
        if (_link.State == ControllerState.AwaitingHello && !IsSuspended
            && Config.Config.Connection.Type == ConnectionType.Serial) SetSilent(true);
    }

    private void SetSilent(bool silent)
    {
        if (Interlocked.Exchange(ref _silent, silent ? 1 : 0) == (silent ? 1 : 0)) return;
        if (silent) AppLog.Info($"No Hello on {Config.Config.Connection.Serial.ComPort} for {_options.SilentAfter.TotalSeconds:F0} s");
        ControllerConfig.OnControllerSilent(silent);
        if (!IsDisposed) ControllerSilentChanged?.Invoke(this, silent);
        UpdateLink();
    }

    // ── Suspension (spec §8.6) ───────────────────────────────────────────────────────────────

    private int _suspensions;

    /// <summary>A firmware probe or flash holds the port: no connection is open or opened.</summary>
    public bool IsSuspended => Volatile.Read(ref _suspensions) > 0;

    /// <summary>Releases the connection (and so the serial port) until the handle is disposed:
    /// the link detaches, a config operation fails and test mode ends, as on a replacement, and
    /// neither a connection config change nor <see cref="Reconnect"/> reopens it meanwhile.
    /// Disposing the last handle restarts the connection from the current config.</summary>
    public IDisposable SuspendConnection()
    {
        Interlocked.Increment(ref _suspensions);
        AppLog.Info("Connection suspended");
        StopConnection();
        return new Suspension(this);
    }

    private void Resume()
    {
        if (Interlocked.Decrement(ref _suspensions) != 0 || IsDisposed || !_started) return;
        AppLog.Info("Connection resumed");
        StartConnection();
    }

    private sealed class Suspension(VolcoRuntime runtime) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) runtime.Resume();
        }
    }

    /// <summary>Detaches and disposes a transport this thread has taken ownership of by removing
    /// it from <see cref="_connection"/> (see its doc). Never called with a lock held.</summary>
    private void RetireTransport(IControllerConnection connection)
    {
        connection.MessageReceived -= OnMessageReceived;
        connection.StatusChanged -= OnTransportStatusChanged;
        connection.Dispose();
    }

    private void OnTransportStatusChanged(object? sender, TransportStatus status)
    {
        // Belt and braces for the restart path above: an old transport's handler invocation that
        // was already in flight on another thread when StartConnection detached it is dropped.
        if (IsDisposed || !ReferenceEquals(sender, Volatile.Read(ref _connection))) return;

        AppLog.Info($"Transport: {status}");
        TransportStatusChanged?.Invoke(this, status);

        if (status == TransportStatus.Open) _link.Attach((IControllerConnection)sender!);
        else
        {
            _link.Detach();
            ControllerConfig.OnDetached();
            ExitTestMode(-1);
            ReleaseAllSliders(cancelSweeps: false);   // the controller cancels a sweep when the host is lost
        }
        UpdateLink();
    }

    private void OnMessageReceived(object? sender, ControllerMessage msg)
    {
        if (IsDisposed) return;

        switch (msg)
        {
            case HelloMessage or AliveMessage:
                // Only the current transport's session: a Hello still in flight from a retired
                // transport must not mark the new session Ready.
                if (!ReferenceEquals(sender, Volatile.Read(ref _connection))) break;
                _link.OnMessage(msg);
                ControllerConfig.OnMessage(sender!, msg);   // after the link: the controller info is stored
                if (msg is HelloMessage hello)
                {
                    AppLog.Info($"Hello: v{hello.Version}, {hello.Chip}, firmware '{hello.FirmwareVersion}', config {hello.ConfigState} " +
                                $"crc {hello.ConfigCrc:X8}, {hello.SliderCount} sliders, buttons [{string.Join(",", hello.Buttons)}]");
                    HelloReceived?.Invoke(this, hello);
                    if (hello.Version == ProtocolVersion.Current) QueryCalibrations(hello.SliderCount);
                }
                break;
            case CalibrationMessage calibration:
                // Same stale-sender filter as Hello.
                if (ReferenceEquals(sender, Volatile.Read(ref _connection))) OnCalibration(calibration);
                break;
            case ConfigDataMessage or ConfigResultMessage:
                // Same stale-sender filter as Hello (spec §8.2).
                if (ReferenceEquals(sender, Volatile.Read(ref _connection))) ControllerConfig.OnMessage(sender!, msg);
                break;
            case SliderMessage slider:
                // Every slider and button raises raw input (hardware-page spec §12.1); in a test
                // (spec §8.5) that is all: nothing reaches the slider processor or the detector.
                // A slider held for a calibration sweep (slider-calibration spec) likewise.
                RawInput?.Invoke(this, new RawInputEventArgs(RawInputKind.Slider, slider.SliderIndex, slider.Value));
                if (InTestMode || IsSliderHeld(slider.SliderIndex)) break;
                AppLog.Debug($"Slider event: Index={slider.SliderIndex}, Value={slider.Value}");
                _sliderProcessor?.HandleSliderEvent(slider.SliderIndex, slider.Value);
                break;
            case ButtonMessage testButton when InTestMode:
            {
                // A press moves the button's test indicator on (an LED toggles, a screen counts).
                var pressed = testButton.State == ButtonState.Pressed;
                var presses = pressed ? CountTestPress(testButton.ButtonIndex) : TestPresses(testButton.ButtonIndex);
                RawInput?.Invoke(this, new RawInputEventArgs(RawInputKind.Button, testButton.ButtonIndex, pressed ? 1 : 0, presses));
                break;
            }
            case ButtonMessage button:
                RawInput?.Invoke(this, new RawInputEventArgs(RawInputKind.Button, button.ButtonIndex,
                                                             button.State == ButtonState.Pressed ? 1 : 0));
                AppLog.Debug($"Button event: Index={button.ButtonIndex}, State={button.State}");
                if (button.State == ButtonState.Pressed)
                {
                    _pressOverUsb[button.ButtonIndex] = sender is not null
                        && ReferenceEquals(sender, Volatile.Read(ref _serialTransport))
                        && ReferenceEquals(sender, Volatile.Read(ref _connection));
                    ButtonActivity?.Invoke(this, button.ButtonIndex);
                }
                var bcfg = Config.Config.Buttons.ElementAtOrDefault(button.ButtonIndex);
                // Fix A: no double-press action configured (in the live mode) -> pass 0, which
                // makes ButtonPressDetector fire Single synchronously on release instead of waiting
                // out a double-press window that can never be satisfied. Long-press is unaffected.
                var doublePressWindowMs = bcfg != null && bcfg.BindingFor(ActiveModeId).DoublePress.Action == ButtonActionType.None
                    ? 0
                    : bcfg?.DoublePressWindowMs ?? 300;
                _buttonPressDetector?.HandleButtonEvent(button.ButtonIndex, button.State,
                    bcfg?.LongPressMs ?? 500, doublePressWindowMs);
                break;
        }
    }

    private void OnButtonPressDetected(object? sender, ButtonPressEventArgs e)
    {
        if (IsDisposed || InTestMode) return;   // second guard (spec §8.5): no action during a test

        AppLog.Info($"Button press detected: Index={e.ButtonIndex}, Type={e.PressType}");
        var config = Config.Config;
        if (e.ButtonIndex < 0 || e.ButtonIndex >= config.Buttons.Count) return;

        var modeId = ActiveModeId;
        var binding = config.Buttons[e.ButtonIndex].BindingFor(modeId);
        var actionConfig = e.PressType switch
        {
            PressType.Single => binding.SinglePress,
            PressType.Double => binding.DoublePress,
            PressType.Long => binding.LongPress,
            _ => null
        };

        if (actionConfig == null) return;
        // An action with a missing or bad setting never runs, and starts no feedback.
        var problems = ActionValidator.Validate(actionConfig, ActionValidationContext.Structural(config));
        if (problems.Count > 0)
        {
            lock (_loggedInvalid)
            {
                if (!_loggedInvalid.TryGetValue(actionConfig, out _))
                {
                    _loggedInvalid.Add(actionConfig, new object());
                    AppLog.Info($"Button {e.ButtonIndex + 1}: {e.PressType} not run: {problems.First().Value}");
                }
            }
            return;
        }
        // Custom-actions spec §7: a press over UDP or WebSocket could be forged, so it never runs code.
        if (ButtonActionTypes.RunsCode(actionConfig.Action)
            && !(_pressOverUsb.TryGetValue(e.ButtonIndex, out var overUsb) && overUsb))
        {
            AppLog.Info($"Button {e.ButtonIndex + 1}: {actionConfig.Action} refused; programs and scripts run only for presses over USB");
            StartFeedback(e.ButtonIndex, binding, actionConfig, ControllerDisplay.UsbOnly);
            return;
        }
        var modeName = config.Modes.Find(p => p.Id == modeId)?.Name ?? modeId;
        var button = e.ButtonIndex;
        var action = ActionFactory.Create(actionConfig, Services, new ActionOrigin(button, e.PressType, modeName)
        {
            ReportError = why => StartFeedback(button, binding, actionConfig, why),
        });

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

        // Feedback first: an error the action reports (at once, or later) replaces it.
        if (action is null && actionConfig.Action != ButtonActionType.None)
            StartFeedback(button, binding, actionConfig, ControllerDisplay.NotAvailable);
        else if (action is not null)
            StartFeedback(button, binding, actionConfig, error: null);

        var modeBefore = ActiveModeId;
        action?.Execute();

        if (alreadyCurrent is not null) ShowDeviceOsd(alreadyCurrent.Id, alreadyCurrent.FriendlyName);

        // Mode actions run synchronously (no audio-thread round trip), so the new mode is
        // already live here.
        if (actionConfig.Action is ButtonActionType.NextMode or ButtonActionType.GoToMode)
        {
            var now = ActiveModeId;
            if (now != modeBefore)
            {
                var name = config.Modes.Find(p => p.Id == now)?.Name ?? now;
                RaiseVolumeChanged(new VolumeChangeNotification(name, 0f, false, OsdKind.Mode));
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
        // Stored names first: the UI re-reads them when it hears of the change (a rename).
        if (RefreshStoredDeviceNames()) Config.RequestSave();
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
        }

        // Every kind: added and removed devices change screens too (an unplugged device), and the
        // link's per-button dedup keeps this cheap.
        PushControllerState();
    }

    /// <summary>Keeps the stored Windows name of every device the config names current, by id
    /// (spec §5.1): each customized device's, and each slider's and action's device reference,
    /// whose name the settings UI shows and falls back to. A device renamed in Windows is then
    /// named anew everywhere a nickname does not override it.</summary>
    private bool RefreshStoredDeviceNames()
    {
        var changed = false;
        var live = Audio.GetDevices(AudioFlow.Render).Concat(Audio.GetDevices(AudioFlow.Capture)).ToList();
        string? NameOf(string? id)
            => string.IsNullOrEmpty(id) ? null
               : live.Find(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))?.FriendlyName;
        foreach (var entry in Config.Config.Devices.ToList())
        {
            if (NameOf(entry.Id) is not { Length: > 0 } name || name == entry.FriendlyName) continue;
            entry.FriendlyName = name;
            changed = true;
        }
        void Refresh(DeviceRefConfig? reference)
        {
            if (reference is null || NameOf(reference.Id) is not { Length: > 0 } name || name == reference.FriendlyName) return;
            reference.FriendlyName = name;
            changed = true;
        }
        foreach (var slider in Config.Config.Sliders.ToList()) Refresh(slider.Device);
        foreach (var button in Config.Config.Buttons.ToList())
            foreach (var binding in button.Actions.Values.ToList())
                foreach (var action in binding.All())
                {
                    Refresh(action.Device);
                    Refresh(action.DeviceB);
                }
        return changed;
    }

    /// <summary>Raises <see cref="VolumeChanged"/> with an <see cref="OsdKind.Mute"/>
    /// notification reflecting the target device's post-write mute flag and real level (the
    /// OSD shows "Muted", or the percentage on unmute), plus its name and icon.</summary>
    private void ShowMuteOsd(ButtonActionConfig actionConfig)
    {
        DeviceLook? look;
        string fallback;
        bool muted;
        float level;
        switch (actionConfig.Action)
        {
            case ButtonActionType.MuteCurrentOutput:
                look = Audio.GetDefaultDevice(AudioFlow.Render) is { } output ? DeviceDisplay.For(Config.Config, output) : null;
                fallback = "Output";
                (level, muted) = Audio.GetStateOrDefault(DeviceRef.DefaultOutput());
                break;
            case ButtonActionType.MuteSpecificDevice:
                var specificDevice = ActionFactory.Resolve(actionConfig.Device);
                if (specificDevice == null) return; // unresolvable device config; nothing to show
                look = DeviceDisplay.For(Config.Config, Audio, actionConfig.Device);
                fallback = "Output";
                (level, muted) = Audio.GetStateOrDefault(specificDevice.Value);
                break;
            case ButtonActionType.MuteCurrentInput:
                look = Audio.GetDefaultDevice(AudioFlow.Capture) is { } input ? DeviceDisplay.For(Config.Config, input) : null;
                fallback = "Microphone";
                (level, muted) = Audio.GetStateOrDefault(DeviceRef.DefaultInput());
                break;
            default:
                return; // non-mute actions don't show the OSD
        }

        RaiseVolumeChanged(new VolumeChangeNotification(look?.Name ?? fallback, level, muted, OsdKind.Mute, look?.Icon));
    }

    /// <summary>Raises <see cref="VolumeChanged"/> with an <see cref="OsdKind.Device"/>
    /// notification: the new default output's name and icon plus its current level and mute flag
    /// (level 0, not muted when the state cannot be read).</summary>
    private void ShowDeviceOsd(string deviceId, string friendlyName)
    {
        var (level, muted) = Audio.GetStateOrDefault(DeviceRef.ById(deviceId));
        var device = Audio.GetDevices(AudioFlow.Render)
            .FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
        var look = device is not null ? DeviceDisplay.For(Config.Config, device) : (DeviceLook?)null;
        var label = look?.Name ?? (string.IsNullOrEmpty(friendlyName) ? "Output" : friendlyName);
        RaiseVolumeChanged(new VolumeChangeNotification(label, level, muted, OsdKind.Device, look?.Icon));
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
    /// After a press of <paramref name="action"/> on <paramref name="button"/>: its feedback for
    /// <see cref="ControllerDisplay.FeedbackSteps"/> steps, replacing any before it. An error
    /// always shows; otherwise only a press of another action than the one the button shows
    /// (<see cref="ControllerDisplay.ShowsWhenPressed"/>) or a Play/pause (its expected state)
    /// needs any, and a press of the shown action ends any other feedback at once.
    /// </summary>
    private void StartFeedback(int button, ButtonBindingConfig binding, ButtonActionConfig action, string? error)
    {
        if (IsDisposed) return;
        var secondary = ControllerDisplay.ShowsWhenPressed(binding, action);
        MediaPlayback? from = null, expect = null;
        if (error is null && action.Action == ButtonActionType.MediaPlayPause
            && ActionStatus.Media(action.MediaApp) is { Present: true, Playback: var now })
        {
            from = now;
            expect = now == MediaPlayback.Playing ? MediaPlayback.Paused : MediaPlayback.Playing;
        }
        if (error is null && !secondary && from is null)
        {
            if (ClearFeedback(button))
            {
                PushControllerState();
                RaiseFeedbackChanged(button);
            }
            return;
        }

        var feedback = new PressFeedback(action, secondary, error, 0, from, expect);
        lock (_feedbackGate)
        {
            if (_feedback.TryGetValue(button, out var old)) old.Timer.Dispose();
            var entry = new FeedbackEntry { Feedback = feedback, Timer = null! };
            entry.Timer = _options.Time.CreateTimer(_ => StepFeedback(button, entry), null,
                                                    ControllerDisplay.FeedbackStep, ControllerDisplay.FeedbackStep);
            _feedback[button] = entry;
        }
        PushControllerState();
        RaiseFeedbackChanged(button);
    }

    /// <summary>One step of <paramref name="entry"/>'s feedback (unless a later press replaced it).</summary>
    private void StepFeedback(int button, FeedbackEntry entry)
    {
        lock (_feedbackGate)
        {
            if (!_feedback.TryGetValue(button, out var current) || !ReferenceEquals(current, entry)) return;
            var step = entry.Feedback.Step + 1;
            if (step >= ControllerDisplay.FeedbackSteps)
            {
                entry.Timer.Dispose();
                _feedback.Remove(button);
            }
            else
            {
                entry.Feedback = step >= ControllerDisplay.ExpectSteps
                    ? entry.Feedback with { Step = step, ExpectFrom = null, Expect = null }   // the real state wins now
                    : entry.Feedback with { Step = step };
            }
        }
        PushControllerState();
        RaiseFeedbackChanged(button);
    }

    /// <summary>Ends the feedback of <paramref name="button"/> (-1: of every button). Returns
    /// whether there was any. Does not push.</summary>
    private bool ClearFeedback(int button = -1)
    {
        lock (_feedbackGate)
        {
            if (button >= 0)
            {
                if (!_feedback.Remove(button, out var entry)) return false;
                entry.Timer.Dispose();
                return true;
            }
            var any = _feedback.Count > 0;
            foreach (var (_, entry) in _feedback) entry.Timer.Dispose();
            _feedback.Clear();
            return any;
        }
    }

    /// <inheritdoc/>
    public PressFeedback? FeedbackFor(int button)
    {
        lock (_feedbackGate)
            return _feedback.TryGetValue(button, out var entry) ? entry.Feedback : null;
    }

    /// <inheritdoc/>
    public int? AnnounceStep => _link.AnnounceStep;

    /// <inheritdoc/>
    public event EventHandler<int>? FeedbackChanged;

    private void RaiseFeedbackChanged(int button)
    {
        if (!IsDisposed) FeedbackChanged?.Invoke(this, button);
    }

    private IReadOnlyDictionary<int, PressFeedback>? FeedbackSnapshot()
    {
        lock (_feedbackGate)
            return _feedback.Count == 0 ? null : _feedback.ToDictionary(p => p.Key, p => p.Value.Feedback);
    }

    /// <summary>A media session, a watched program or a script run changed (any thread).</summary>
    public event EventHandler? ActionStateChanged;

    /// <summary>A media session, a watched program or a script run changed: the controller follows.</summary>
    private void OnActionStateChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        PushControllerState();
        ActionStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Polls the programs that Open a program actions in the live mode name.</summary>
    private void WatchPrograms()
    {
        if (_programs is null || IsDisposed) return;
        var modeId = ActiveModeId;
        _programs.Watch(Config.Config.Buttons
            .SelectMany(b => b.BindingFor(modeId).All())
            .Where(a => a.Action == ButtonActionType.OpenProgram && a.Command is not null)
            .Select(a => a.Command!)
            .ToList());
    }

    /// <summary>Sends whatever changed on the controller (the link dedups per button).</summary>
    private void PushControllerState()
    {
        if (!IsDisposed) _link.Push();
    }

    /// <summary>Stores the controller's hardware before the link pushes (spec §3.4); saves only
    /// when it changed. Also arms the slider sync: every slider's position follows the Hello.</summary>
    private void OnHello(object? sender, HelloMessage hello)
    {
        // Raised on the receive path before the Slider frames that follow the Hello are routed.
        _sliderProcessor?.BeginSync();
        if (hello.Version != ProtocolVersion.Current) return;  // version-only: keep the last good summary

        var info = new ControllerInfoConfig
        {
            ProtocolVersion = hello.Version, Chip = hello.Chip, FirmwareVersion = hello.FirmwareVersion,
            Sliders = hello.SliderCount, Buttons = hello.Buttons.ToList(),
        };
        if (info.SameAs(Config.Config.Controller)) return;
        Config.Config.Controller = info;
        Config.RequestSave();
        if (!IsDisposed) ControllerInfoChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Any non-connection save (actions, modes, nicknames, icons): the controller
    /// follows. A deleted live mode falls back to the first one and is pushed instantly.</summary>
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        WatchPrograms();
        var stored = Volatile.Read(ref _activeModeId);
        if (stored is not null && !Config.Config.Modes.Exists(p => p.Id == stored)
            && Interlocked.CompareExchange(ref _activeModeId, null, stored) == stored)
        {
            ActiveModeChanged?.Invoke(this, ActiveModeId);
            _link.PushAll();
            return;
        }
        _link.Push();
    }

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        // Handler is invoked only when connection config changes
        if (IsUserDisconnected)
        {
            AppLog.Info("Connection configuration changed while disconnected; used on Connect");
            return;
        }
        AppLog.Info("Connection configuration changed, restarting connection");
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
        Config.ConfigChanged -= OnSettingsChanged;

        // Before the transport goes: nothing may be sent to it while it is being retired. An
        // operation in flight ends as if the connection were replaced.
        ControllerConfig.Dispose();
        lock (_silenceGate)
        {
            _silenceTimer?.Dispose();
            _silenceTimer = null;
        }

        bool wasTesting;
        lock (_testGate)
        {
            wasTesting = _testGeneration != 0;
            _testGeneration = 0;
        }
        // The one event raised after Dispose began (spec §8.5): a page holding a test handle learns
        // that the test is over.
        if (wasTesting) InTestModeChanged?.Invoke(this, false);
        ReleaseAllSliders(cancelSweeps: true);   // before the link goes, so the Cancel can still be sent
        _link.Dispose();

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

        if (_media is not null) _media.Changed -= OnActionStateChanged;
        if (_programs is not null)
        {
            _programs.Changed -= OnActionStateChanged;
            _programs.Watch(Array.Empty<CommandConfig>());
        }
        ClearFeedback();
        if (_scripts is not null)
        {
            _scripts.Changed -= OnActionStateChanged;
            _scripts.Dispose();   // stops the flash timers; running scripts keep running
        }
    }
}
