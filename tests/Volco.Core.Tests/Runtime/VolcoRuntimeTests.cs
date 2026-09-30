using System.Collections.Concurrent;
using System.Diagnostics;
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Input;
using Volco.Core.Runtime;
using Volco.Core.Tests.Audio;
using Volco.Core.Tests.Config;
using Xunit;

namespace Volco.Core.Tests.Runtime;

/// <summary>
/// Drives <see cref="VolcoRuntime"/> headless with <see cref="FakeAudioEngine"/> and
/// <see cref="FakeControllerConnection"/>. Anything timer-driven (slider coalescing, button
/// press classification, post-connect re-pushes, ConfigManager's save debounce) is awaited with
/// a bounded poll, never a fixed sleep; "nothing more happens" is asserted over a short window.
/// </summary>
public sealed class VolcoRuntimeTests : IDisposable
{
    private const string Speakers = "spk";
    private const string Headphones = "hp";
    private const string Microphone = "mic";

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan QuietWindow = TimeSpan.FromMilliseconds(150);

    /// <summary>No timed re-pushes, so frame counts only move when the test makes them.</summary>
    private static readonly VolcoRuntimeOptions NoTimedRepushes = new(Array.Empty<TimeSpan>());

    private readonly FakeAudioEngine _engine = new();
    private readonly List<FakeControllerConnection> _connections = new(); // guarded by itself
    private readonly List<VolcoRuntime> _runtimes = new();
    private readonly List<string> _tempDirs = new();

    public VolcoRuntimeTests()
    {
        var speakers = new AudioDevice(Speakers, "Speakers", AudioFlow.Render, true);
        var headphones = new AudioDevice(Headphones, "Headphones", AudioFlow.Render, false);
        var mic = new AudioDevice(Microphone, "Mic", AudioFlow.Capture, true);
        _engine.Devices = new List<AudioDevice> { speakers, headphones, mic };
        _engine.DefaultRender = speakers;
        _engine.DefaultCapture = mic;
        _engine.DeviceStates[Speakers] = (0.5f, false);
        _engine.DeviceStates[Headphones] = (0.5f, false);
        _engine.DeviceStates[Microphone] = (0.5f, false);
    }

    public void Dispose()
    {
        foreach (var runtime in _runtimes) runtime.Dispose();
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---- helpers ---------------------------------------------------------------------------

    private IControllerConnection? Factory(ConnectionConfig config)
    {
        var connection = new FakeControllerConnection(config);
        lock (_connections) _connections.Add(connection);
        return connection;
    }

    private int ConnectionCount { get { lock (_connections) return _connections.Count; } }

    private FakeControllerConnection Connection(int index)
    {
        lock (_connections) return _connections[index];
    }

    /// <summary>A loaded ConfigManager over a fresh temp dir, with buttons 0/1 muting the
    /// default output/input, the toggle pair set to speakers/headphones, and a short
    /// double-press window so single presses classify quickly.</summary>
    private ConfigManager NewConfig(Action<AppConfig>? configure = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"volco-runtime-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);

        var cm = new ConfigManager(Path.Combine(dir, "config.json"));
        cm.Load(); // no file -> defaults (schema v2, 5 sliders, 3 buttons)

        var c = cm.Config;
        c.Buttons[0].SinglePress = new ButtonActionConfig { Action = ButtonActionType.MuteCurrentOutput };
        c.Buttons[1].SinglePress = new ButtonActionConfig { Action = ButtonActionType.MuteCurrentInput };
        foreach (var b in c.Buttons) b.DoublePressWindowMs = 30;
        c.OutputDevices.Device1 = new DeviceRefConfig { Id = Speakers, FriendlyName = "Speakers" };
        c.OutputDevices.Device2 = new DeviceRefConfig { Id = Headphones, FriendlyName = "Headphones" };
        configure?.Invoke(c);
        return cm;
    }

    private VolcoRuntime StartRuntime(ConfigManager? config = null, bool audioReady = true,
                                      VolcoRuntimeOptions? options = null)
    {
        var runtime = new VolcoRuntime(config ?? NewConfig(), _engine, audioReady, Factory,
                                       options ?? NoTimedRepushes);
        _runtimes.Add(runtime);
        runtime.Start();
        return runtime;
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var limit = timeout ?? WaitTimeout;
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > limit) return condition();
            Thread.Sleep(5);
        }
        return true;
    }

    /// <summary>True when <paramref name="read"/> keeps returning its current value for the
    /// whole <paramref name="window"/>. Fails as soon as it changes.</summary>
    private static bool StaysUnchanged(Func<int> read, TimeSpan? window = null)
    {
        var initial = read();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < (window ?? QuietWindow))
        {
            if (read() != initial) return false;
            Thread.Sleep(5);
        }
        return read() == initial;
    }

    private static void Press(FakeControllerConnection connection, int button)
    {
        connection.RaiseMessage(new ButtonMessage(button, ButtonState.Pressed));
        connection.RaiseMessage(new ButtonMessage(button, ButtonState.Released));
    }

    private static bool ContainsFrame(IEnumerable<byte[]> frames, byte[] expected)
        => frames.Any(f => f.AsSpan().SequenceEqual(expected));

    /// <summary>Thread-safe point-in-time copy of one of <see cref="_engine"/>'s call-recording
    /// lists (see <see cref="FakeAudioEngine.Snapshot{T}"/>). Use this instead of indexing or
    /// enumerating those lists directly after a <see cref="WaitUntil"/> — the wait only proves
    /// the count moved, not that the item at that index has finished being stored.</summary>
    private IReadOnlyList<T> Snapshot<T>(List<T> list) => _engine.Snapshot(list);

    /// <summary>Thread-safe event recorder: runtime events can fire on any thread.</summary>
    private sealed class Recorder<T>
    {
        private readonly ConcurrentQueue<T> _items = new();
        public void Handler(object? sender, T item) => _items.Enqueue(item);
        public IReadOnlyList<T> Items => _items.ToArray();
        public int Count => _items.Count;
    }

    // ---- routing ---------------------------------------------------------------------------

    [Fact]
    public void SliderMessage_RoutesToSliderProcessor()
    {
        var runtime = StartRuntime(); // slider 0 defaults to Device / current default output
        var levels = new Recorder<SliderLevelEventArgs>();
        var volumes = new Recorder<VolumeChangeNotification>();
        runtime.SliderLevelChanged += levels.Handler;
        runtime.VolumeChanged += volumes.Handler;

        Connection(0).RaiseMessage(new SliderMessage(0, 4095));

        Assert.True(WaitUntil(() => _engine.DeviceVolumeCalls.Count > 0), "no SetDeviceVolume call");
        var deviceVolumeCalls = Snapshot(_engine.DeviceVolumeCalls);
        Assert.Equal(DeviceRef.DefaultOutput(), deviceVolumeCalls[0].Device);
        Assert.Equal(1.0, (double)deviceVolumeCalls[0].Level, 3);

        // SliderProcessor's events are re-raised as the runtime's own.
        Assert.True(WaitUntil(() => levels.Count > 0 && volumes.Count > 0), "slider events not re-raised");
        Assert.Equal(0, levels.Items[0].SliderIndex);
        Assert.Equal(OsdKind.Volume, volumes.Items[0].Kind);
    }

    [Fact]
    public void ButtonPress_MuteCurrentOutput_TogglesDefaultOutput()
    {
        StartRuntime();

        Press(Connection(0), 0);

        Assert.True(WaitUntil(() => _engine.ToggleMuteCalls.Count > 0), "no ToggleDeviceMute call");
        Assert.Contains(DeviceRef.DefaultOutput(), Snapshot(_engine.ToggleMuteCalls));
    }

    // ---- Fix A: no double-press configured -> single press dispatches synchronously ---------

    [Fact]
    public void SinglePress_NoDoublePressConfigured_TogglesMuteSynchronously()
    {
        // NewConfig() gives button 0 SinglePress=MuteCurrentOutput and leaves DoublePress at its
        // default (Action=None), so the runtime must pass doublePressWindowMs=0 down to the
        // detector, which fires Single synchronously on release - no polling needed here.
        StartRuntime();
        var connection = Connection(0);

        connection.RaiseMessage(new ButtonMessage(0, ButtonState.Pressed));
        connection.RaiseMessage(new ButtonMessage(0, ButtonState.Released));

        Assert.Single(Snapshot(_engine.ToggleMuteCalls));
    }

    [Fact]
    public void SinglePress_WithDoublePressConfigured_WaitsForWindowBeforeDispatching()
    {
        var config = NewConfig(c =>
        {
            c.Buttons[0].DoublePress = new ButtonActionConfig { Action = ButtonActionType.MuteCurrentInput };
            c.Buttons[0].DoublePressWindowMs = 60;
        });
        StartRuntime(config);
        var connection = Connection(0);

        connection.RaiseMessage(new ButtonMessage(0, ButtonState.Pressed));
        connection.RaiseMessage(new ButtonMessage(0, ButtonState.Released));

        // Immediately after the release, the single press must still be pending: a double-press
        // action IS configured, so the detector must wait out the window rather than dispatch now.
        Assert.Empty(Snapshot(_engine.ToggleMuteCalls));

        Assert.True(WaitUntil(() => _engine.ToggleMuteCalls.Count > 0),
                    "single press never dispatched after its double-press window elapsed");
    }

    // ---- mute OSD correlation --------------------------------------------------------------

    [Fact]
    public void MutePress_OwnChangeOnTargetDevice_RaisesMuteOsd()
    {
        var runtime = StartRuntime();
        var osd = new Recorder<VolumeChangeNotification>();
        runtime.VolumeChanged += osd.Handler;

        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => _engine.ToggleMuteCalls.Count > 0), "press never executed");

        // The engine's own write lands: state is now muted, and the callback reports it.
        _engine.DeviceStates[Speakers] = (0.5f, true);
        _engine.RaiseDeviceVolumeChanged(Speakers, 0.5f, muted: true, isOwnChange: true);
        // A second own-change must not show it again: the pending OSD is consumed.
        _engine.RaiseDeviceVolumeChanged(Speakers, 0.5f, muted: true, isOwnChange: true);

        var n = Assert.Single(osd.Items);
        Assert.Equal(OsdKind.Mute, n.Kind);
        Assert.True(n.Muted);
        Assert.Equal("Speakers", n.Label);
    }

    [Fact]
    public void MutePress_ExternalChange_DoesNotRaiseOsd()
    {
        var runtime = StartRuntime();
        var osd = new Recorder<VolumeChangeNotification>();
        runtime.VolumeChanged += osd.Handler;

        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => _engine.ToggleMuteCalls.Count > 0), "press never executed");

        _engine.DeviceStates[Speakers] = (0.5f, true);
        _engine.RaiseDeviceVolumeChanged(Speakers, 0.5f, muted: true, isOwnChange: false);

        Assert.Empty(osd.Items);
    }

    /// <summary>Covers two ways a mute-specific-device press can target a device that never
    /// resolves to a real, currently-connected endpoint: no id at all (falls back to a friendly
    /// name that matches nothing, so the runtime's expected-device resolution returns null and no
    /// OSD is ever pending), and a non-empty id that matches no known device (the id is trusted
    /// as-is, so an OSD-pending record IS created — but for a device id nothing will ever report a
    /// change for, so no OSD ever fires either way). Both must behave identically from the
    /// caller's point of view: press executes, no OSD shows.</summary>
    [Theory]
    [InlineData("", "Unplugged Headset")]
    [InlineData("absent-id", "Unplugged Headset")]
    public void MutePress_UnresolvableDevice_NeverShowsOsd(string id, string friendlyName)
    {
        var config = NewConfig(c => c.Buttons[0].SinglePress = new ButtonActionConfig
        {
            Action = ButtonActionType.MuteSpecificDevice,
            Device = new DeviceRefConfig { Id = id, FriendlyName = friendlyName },
        });
        var runtime = StartRuntime(config);
        var osd = new Recorder<VolumeChangeNotification>();
        runtime.VolumeChanged += osd.Handler;

        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => _engine.ToggleMuteCalls.Count > 0), "press never executed");
        var expected = string.IsNullOrEmpty(id) ? DeviceRef.ByFriendlyName(friendlyName) : DeviceRef.ById(id);
        Assert.Equal(expected, Snapshot(_engine.ToggleMuteCalls)[0]);

        // Own changes on devices that are NOT the (unresolvable) target must not pop an OSD.
        _engine.DeviceStates[Speakers] = (0.5f, true);
        _engine.RaiseDeviceVolumeChanged(Speakers, 0.5f, muted: true, isOwnChange: true);
        _engine.RaiseDeviceVolumeChanged(Headphones, 0.3f, muted: false, isOwnChange: true);

        Assert.Empty(osd.Items);
    }

    // ---- controller-state pushes -----------------------------------------------------------

    [Fact]
    public void Connected_PushesStateImmediately()
    {
        var runtime = StartRuntime();
        var statuses = new Recorder<ConnectionStatus>();
        runtime.ConnectionStatusChanged += statuses.Handler;
        Assert.Equal(ConnectionStatus.Disconnected, runtime.ConnectionStatus);

        Connection(0).SetStatus(ConnectionStatus.Connected);

        var frames = Connection(0).SentFrames;
        Assert.True(ContainsFrame(frames, ProtocolEncoder.EncodeMuteState(0, false)), "button 0 mute frame missing");
        Assert.True(ContainsFrame(frames, ProtocolEncoder.EncodeMuteState(1, false)), "button 1 mute frame missing");
        Assert.True(ContainsFrame(frames, ProtocolEncoder.EncodeActiveDevice(0)), "active-device frame missing");
        Assert.Equal(ConnectionStatus.Connected, runtime.ConnectionStatus);
        Assert.Equal(new[] { ConnectionStatus.Connected }, statuses.Items);
    }

    [Fact]
    public void OwnVolumeChanges_DoNotResendIdenticalFrames()
    {
        StartRuntime();
        var connection = Connection(0);
        connection.SetStatus(ConnectionStatus.Connected);
        var baseline = connection.SentCount;
        Assert.True(baseline > 0);

        // A Device-mapped slider drag: the engine reports each of its own volume writes, mute
        // never changes, so the encoded LED state never changes either.
        for (int i = 0; i < 20; i++)
        {
            var level = i / 20f;
            _engine.DeviceStates[Speakers] = (level, false);
            _engine.RaiseDeviceVolumeChanged(Speakers, level, muted: false, isOwnChange: true);
        }

        Assert.Equal(baseline, connection.SentCount);
    }

    [Fact]
    public void MuteChange_ResendsFrames()
    {
        StartRuntime();
        var connection = Connection(0);
        connection.SetStatus(ConnectionStatus.Connected);
        var baseline = connection.SentCount;

        _engine.DeviceStates[Speakers] = (0.5f, true);
        _engine.RaiseDeviceVolumeChanged(Speakers, 0.5f, muted: true, isOwnChange: false);

        Assert.True(connection.SentCount > baseline, "mute flip did not resend frames");
        var newFrames = connection.SentFrames.Skip(baseline);
        Assert.True(ContainsFrame(newFrames, ProtocolEncoder.EncodeMuteState(0, true)), "muted frame not sent");
    }

    [Fact]
    public void Connected_RepushesAfterEachConfiguredDelay()
    {
        var config = NewConfig();
        StartRuntime(config, options: new VolcoRuntimeOptions(
            new[] { TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(60) }));
        var connection = Connection(0);
        var framesPerPush = ControllerStateReporter.BuildFrames(config.Config, _engine).Count;
        Assert.True(framesPerPush > 0);

        connection.SetStatus(ConnectionStatus.Connected);

        // Immediate push + one forced re-push per configured delay, then nothing more.
        Assert.True(WaitUntil(() => connection.SentCount >= 3 * framesPerPush),
                    $"expected {3 * framesPerPush} frames, got {connection.SentCount}");
        Assert.True(StaysUnchanged(() => connection.SentCount), "more pushes than configured delays");
        Assert.Equal(3 * framesPerPush, connection.SentCount);
    }

    [Fact]
    public void FirstMessageAfterConnect_ForcesOneRepush()
    {
        StartRuntime();
        var connection = Connection(0);
        connection.SetStatus(ConnectionStatus.Connected);
        var afterConnect = connection.SentCount;
        Assert.True(afterConnect > 0);

        // Slider index 10 is beyond the configured sliders, so it touches nothing but the
        // first-message flag.
        connection.RaiseMessage(new SliderMessage(10, 0));
        Assert.Equal(2 * afterConnect, connection.SentCount);

        connection.RaiseMessage(new SliderMessage(10, 0));
        Assert.Equal(2 * afterConnect, connection.SentCount);
    }

    // ---- transport restart -----------------------------------------------------------------

    [Fact]
    public void Restart_ForwardsStatusFromNewTransportOnly()
    {
        var config = NewConfig();
        var runtime = StartRuntime(config);
        var statuses = new Recorder<ConnectionStatus>();
        runtime.ConnectionStatusChanged += statuses.Handler;
        var oldConnection = Connection(0);
        Assert.Equal(1, oldConnection.ConnectCount);

        config.Config.Connection.Type = ConnectionType.WebSocket;
        config.RequestSave(connectionChanged: true); // debounced, raises ConnectionConfigChanged

        // Wait for Connect() on the new transport, not just its creation: the factory runs before
        // the transport is published and its handlers attached, and Connect() is the last step of
        // the restart on the ConfigManager debounce thread.
        Assert.True(WaitUntil(() => ConnectionCount == 2 && Connection(1).ConnectCount == 1),
                    "transport was not restarted");
        var newConnection = Connection(1);
        Assert.Equal(1, oldConnection.DisposeCount);
        Assert.Equal(ConnectionType.WebSocket, newConnection.Config.Type);
        Assert.Equal(1, newConnection.ConnectCount);

        oldConnection.SetStatus(ConnectionStatus.Connected);
        Assert.Empty(statuses.Items);
        Assert.Equal(0, oldConnection.SentCount);

        newConnection.SetStatus(ConnectionStatus.Connected);
        Assert.Equal(new[] { ConnectionStatus.Connected }, statuses.Items);
        Assert.Equal(ConnectionStatus.Connected, runtime.ConnectionStatus);
        Assert.True(newConnection.SentCount > 0, "state not pushed to the new transport");
    }

    /// <summary>Pins Review Focus #2's user-visible case: restarting while the OLD transport is
    /// still Connected (a Serial → WebSocket/UDP switch mid-session), not merely Disconnected as
    /// in <see cref="Restart_ForwardsStatusFromNewTransportOnly"/>. This is the only path that
    /// exercises the runtime's own synthetic Disconnected raise in StartConnection — in
    /// production it is the only thing that stops a settings page showing a stale "Connected"
    /// when the new transport (e.g. a WebSocket/UDP listener with no peer yet) sits Disconnected
    /// and never raises anything itself.</summary>
    [Fact]
    public void Restart_FromConnected_ReportsDisconnectedOnce()
    {
        var config = NewConfig();
        var runtime = StartRuntime(config);
        var statuses = new Recorder<ConnectionStatus>();
        runtime.ConnectionStatusChanged += statuses.Handler;
        var oldConnection = Connection(0);

        oldConnection.SetStatus(ConnectionStatus.Connected);
        Assert.Equal(new[] { ConnectionStatus.Connected }, statuses.Items);

        config.Config.Connection.Type = ConnectionType.WebSocket;
        config.RequestSave(connectionChanged: true); // debounced, raises ConnectionConfigChanged

        Assert.True(WaitUntil(() => ConnectionCount == 2 && Connection(1).ConnectCount == 1),
                    "transport was not restarted");
        var newConnection = Connection(1);

        Assert.Equal(new[] { ConnectionStatus.Connected, ConnectionStatus.Disconnected }, statuses.Items);
        Assert.Equal(ConnectionStatus.Disconnected, runtime.ConnectionStatus);

        newConnection.SetStatus(ConnectionStatus.Connected);
        Assert.Equal(new[]
        {
            ConnectionStatus.Connected, ConnectionStatus.Disconnected, ConnectionStatus.Connected,
        }, statuses.Items);
    }

    // ---- config migration ------------------------------------------------------------------

    private ConfigManager LoadV1Config(out string path)
    {
        path = ConfigMigratorTests.WriteTempConfig(ConfigMigratorTests.V1Json);
        _tempDirs.Add(Path.GetDirectoryName(path)!);
        _engine.Devices = ConfigMigratorTests.Devices.ToList();
        var cm = new ConfigManager(path);
        cm.Load();
        Assert.True(ConfigMigrator.NeedsMigration(cm.Config));
        return cm;
    }

    [Fact]
    public void Start_AudioNotReady_SkipsMigration()
    {
        var cm = LoadV1Config(out var path);
        var original = File.ReadAllText(path);

        var runtime = StartRuntime(cm, audioReady: false);

        Assert.True(runtime.AudioEngineFailed);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "config.v1.bak")));
        Assert.True(ConfigMigrator.NeedsMigration(cm.Config));
        // Everything else still starts.
        Assert.Equal(1, ConnectionCount);
    }

    [Fact]
    public void Start_AudioReady_MigratesV1Config()
    {
        var cm = LoadV1Config(out var path);

        var runtime = StartRuntime(cm, audioReady: true);

        Assert.False(runtime.AudioEngineFailed);
        Assert.Equal(AppConfig.CurrentSchemaVersion, cm.Config.SchemaVersion);
        Assert.Equal("dev-2", cm.Config.Sliders[0].Device!.Id);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "config.v1.bak")));

        // Saved, not just migrated in memory.
        var reloaded = new ConfigManager(path);
        reloaded.Load();
        Assert.Equal(AppConfig.CurrentSchemaVersion, reloaded.Config.SchemaVersion);
    }

    // ---- device forwarding and lifetime ----------------------------------------------------

    // ---- Fix C: healing blank device ids -----------------------------------------------------

    [Fact]
    public void Start_HealsBlankOutputDeviceId_WhenNameMatchesAPresentDevice()
    {
        var config = NewConfig(c =>
        {
            c.OutputDevices.Device1 = new DeviceRefConfig { Id = "", FriendlyName = "Speakers" };
        });
        var path = config.ConfigPath;

        StartRuntime(config);

        Assert.Equal(Speakers, config.Config.OutputDevices.Device1.Id);
        Assert.Contains(Speakers, File.ReadAllText(path)); // saved to disk, not just in memory
    }

    [Fact]
    public void Start_AudioNotReady_DoesNotHealDeviceIds()
    {
        var config = NewConfig(c =>
        {
            c.OutputDevices.Device1 = new DeviceRefConfig { Id = "", FriendlyName = "Speakers" };
        });

        StartRuntime(config, audioReady: false);

        Assert.Equal("", config.Config.OutputDevices.Device1.Id);
    }

    [Fact]
    public void DeviceAdded_HealsBlankDeviceId_ForADeviceThatWasAbsentAtStart()
    {
        const string NewDeviceId = "new-dev";
        var config = NewConfig(c =>
        {
            // Not in _engine.Devices yet - Start()'s heal pass must leave this unhealed.
            c.OutputDevices.Device1 = new DeviceRefConfig { Id = "", FriendlyName = "New Speakers" };
        });

        StartRuntime(config);
        Assert.Equal("", config.Config.OutputDevices.Device1.Id);

        var newDevice = new AudioDevice(NewDeviceId, "New Speakers", AudioFlow.Render, false);
        _engine.Devices.Add(newDevice);
        _engine.RaiseDeviceChanged(DeviceChangeKind.Added, newDevice);

        Assert.True(WaitUntil(() => config.Config.OutputDevices.Device1.Id == NewDeviceId),
                    "device id was not healed after DevicesChanged(Added)");
    }

    [Fact]
    public void HealedOutputDevice_TogglePressEndsUpSettingTheHealedDevice()
    {
        var config = NewConfig(c =>
        {
            c.OutputDevices.Device1 = new DeviceRefConfig { Id = Speakers, FriendlyName = "Speakers" };
            c.OutputDevices.Device2 = new DeviceRefConfig { Id = "", FriendlyName = "Headphones" };
            c.Buttons[2].SinglePress = new ButtonActionConfig { Action = ButtonActionType.ToggleOutputDevice };
        });

        StartRuntime(config);
        Assert.Equal(Headphones, config.Config.OutputDevices.Device2.Id); // healed at Start()

        Press(Connection(0), 2);

        Assert.True(WaitUntil(() => _engine.SetDefaultCalls.Count > 0), "no SetDefaultDevice call");
        Assert.Contains(Snapshot(_engine.SetDefaultCalls), c => c.EndpointId == Headphones);
    }

    // ---- Fix D: GetSliderLevel ---------------------------------------------------------------

    [Fact]
    public void GetSliderLevel_NullBeforeStart_ReflectsMessageAfterStart_NullAfterDispose()
    {
        var config = NewConfig();
        var runtime = new VolcoRuntime(config, _engine, audioReady: true, Factory, NoTimedRepushes);

        Assert.Null(runtime.GetSliderLevel(0)); // before Start()

        runtime.Start();
        _runtimes.Add(runtime); // still covered by test-teardown Dispose (idempotent)

        // Slider 0 defaults to Device / current default output (see NewConfig).
        Connection(0).RaiseMessage(new SliderMessage(0, 4095));
        Assert.True(WaitUntil(() => runtime.GetSliderLevel(0) is { } lvl && lvl > 0.9f),
                    "slider level was not reflected after a slider message");

        runtime.Dispose();
        Assert.Null(runtime.GetSliderLevel(0)); // after Dispose()
    }

    [Fact]
    public void DevicesChanged_IsForwardedForEveryKind()
    {
        var runtime = StartRuntime();
        var changes = new Recorder<DevicesChangedEventArgs>();
        runtime.DevicesChanged += changes.Handler;

        _engine.RaiseDeviceChanged(DeviceChangeKind.Added, _engine.Devices[1]);
        _engine.RaiseDeviceChanged(DeviceChangeKind.DefaultChanged, _engine.Devices[0]);

        Assert.Equal(new[] { DeviceChangeKind.Added, DeviceChangeKind.DefaultChanged },
                     changes.Items.Select(c => c.Kind));
    }

    [Fact]
    public void Dispose_DisposesTransport_NotEngine()
    {
        var runtime = StartRuntime(options: new VolcoRuntimeOptions(new[] { TimeSpan.FromMilliseconds(40) }));
        var connection = Connection(0);
        connection.SetStatus(ConnectionStatus.Connected);

        var events = 0;
        runtime.VolumeChanged += (_, _) => Interlocked.Increment(ref events);
        runtime.SliderLevelChanged += (_, _) => Interlocked.Increment(ref events);
        runtime.ConnectionStatusChanged += (_, _) => Interlocked.Increment(ref events);
        runtime.DevicesChanged += (_, _) => Interlocked.Increment(ref events);

        runtime.Dispose();
        runtime.Dispose(); // idempotent

        Assert.Equal(1, connection.DisposeCount);
        Assert.False(_engine.Disposed);

        var sentAtDispose = connection.SentCount;
        _engine.DeviceStates[Speakers] = (0.5f, true);
        _engine.RaiseDeviceVolumeChanged(Speakers, 0.5f, muted: true, isOwnChange: true);
        _engine.RaiseDeviceChanged(DeviceChangeKind.DefaultChanged, _engine.Devices[1]);
        connection.SetStatus(ConnectionStatus.Disconnected);
        connection.RaiseMessage(new SliderMessage(0, 1000));

        // Covers the slider coalescing tick and the (cancelled) 40 ms re-push timer.
        Assert.True(StaysUnchanged(() => Volatile.Read(ref events) + connection.SentCount + _engine.DeviceVolumeCalls.Count));
        Assert.Equal(0, Volatile.Read(ref events));
        Assert.Equal(sentAtDispose, connection.SentCount);
        Assert.Empty(_engine.DeviceVolumeCalls);
    }
}
