using System.Collections.Concurrent;
using System.Diagnostics;
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Input;
using Volco.Core.Runtime;
using Volco.Core.Tests.Audio;
using Volco.Core.Tests.Config;
using Xunit;

namespace Volco.Core.Tests.Runtime;

/// <summary>
/// Drives <see cref="VolcoRuntime"/> headless with <see cref="FakeAudioEngine"/> and
/// <see cref="FakeControllerConnection"/>. Anything timer-driven (slider coalescing, button
/// press classification, ConfigManager's save debounce) is awaited with
/// a bounded poll, never a fixed sleep; "nothing more happens" is asserted over a short window.
/// </summary>
public sealed class VolcoRuntimeTests : IDisposable
{
    private const string Speakers = "spk";
    private const string Headphones = "hp";
    private const string Microphone = "mic";

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan QuietWindow = TimeSpan.FromMilliseconds(150);

    /// <summary>No HelloRequest retries or heartbeats inside a test unless it asks for them.</summary>
    private static readonly VolcoRuntimeOptions Quiet = new()
    {
        Link = new ControllerLinkOptions(TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.FromHours(1)),
    };

    private static readonly HelloMessage LedHello = new(ProtocolVersion.Current, 5,
        new[] { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair });

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

    /// <summary>A loaded ConfigManager over a fresh temp dir, with every slider on the default
    /// output, buttons 0/1 muting the default output/input, every toggle action's pair defaulting
    /// to speakers/headphones, and a short
    /// double-press window so single presses classify quickly.</summary>
    private ConfigManager NewConfig(Action<AppConfig>? configure = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"volco-runtime-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);

        var cm = new ConfigManager(Path.Combine(dir, "config.json"));
        cm.Load(); // no file -> defaults (5 sliders, 3 buttons, all not set up)

        var c = cm.Config;
        // The schema v2 defaults these tests were written against: every slider drives the default
        // output device.
        foreach (var slider in c.Sliders) slider.Type = SliderMappingType.Device;
        c.Buttons[0].Default().SinglePress = new ButtonActionConfig { Action = ButtonActionType.MuteCurrentOutput };
        c.Buttons[1].Default().SinglePress = new ButtonActionConfig { Action = ButtonActionType.MuteCurrentInput };
        foreach (var b in c.Buttons) b.DoublePressWindowMs = 30;
        configure?.Invoke(c);

        // Schema v3 keeps the toggle pair on the action: give every toggle action that names no
        // devices the speakers/headphones pair the old global setting carried.
        foreach (var action in c.Buttons.SelectMany(b => b.Actions.Values).SelectMany(x => x.All()))
        {
            if (action.Action != ButtonActionType.ToggleOutputDevice) continue;
            action.Device ??= new DeviceRefConfig { Id = Speakers, FriendlyName = "Speakers" };
            action.DeviceB ??= new DeviceRefConfig { Id = Headphones, FriendlyName = "Headphones" };
        }
        return cm;
    }

    private VolcoRuntime StartRuntime(ConfigManager? config = null, bool audioReady = true,
                                      VolcoRuntimeOptions? options = null)
    {
        var runtime = new VolcoRuntime(config ?? NewConfig(), _engine, audioReady, Factory,
                                       options ?? Quiet);
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

    /// <summary>Transport connected and the controller said hello.</summary>
    private static void ConnectAndGreet(FakeControllerConnection connection, HelloMessage? hello = null)
    {
        connection.SetStatus(TransportStatus.Open);
        connection.RaiseMessage(hello ?? LedHello);
    }

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
    public void SliderMessage_DefaultOutputMuted_RaisesMutedVolumeOsd()
    {
        _engine.DeviceStates[Speakers] = (0.5f, true);
        var runtime = StartRuntime(); // slider 0 defaults to Device / current default output
        var volumes = new Recorder<VolumeChangeNotification>();
        runtime.VolumeChanged += volumes.Handler;

        Connection(0).RaiseMessage(new SliderMessage(0, 4095));

        Assert.True(WaitUntil(() => volumes.Count > 0), "no slider OSD raised");
        Assert.Equal(OsdKind.Volume, volumes.Items[0].Kind);
        Assert.True(volumes.Items[0].Muted);
    }

    [Fact]
    public void SlidersAfterHello_SetVolumeWithoutOsd()
    {
        var runtime = StartRuntime(); // slider 0 defaults to Device / current default output
        var levels = new Recorder<SliderLevelEventArgs>();
        var volumes = new Recorder<VolumeChangeNotification>();
        runtime.SliderLevelChanged += levels.Handler;
        runtime.VolumeChanged += volumes.Handler;

        // The firmware follows every Hello with each slider's position.
        ConnectAndGreet(Connection(0));
        Connection(0).RaiseMessage(new SliderMessage(0, 4095));

        Assert.True(WaitUntil(() => levels.Count > 0), "sync value did not reach the slider processor");
        Assert.Equal(1.0, (double)Snapshot(_engine.DeviceVolumeCalls)[0].Level, 3);
        // The OSD would have fired in the same tick as the level, so it is already counted if raised.
        Assert.False(WaitUntil(() => volumes.Count > 0, QuietWindow), "a sync value raised the volume OSD");
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
            c.Buttons[0].Default().DoublePress = new ButtonActionConfig { Action = ButtonActionType.MuteCurrentInput };
            c.Buttons[0].DoublePressWindowMs = 500;
        });
        StartRuntime(config);
        var connection = Connection(0);

        connection.RaiseMessage(new ButtonMessage(0, ButtonState.Pressed));
        connection.RaiseMessage(new ButtonMessage(0, ButtonState.Released));

        // Immediately after the release, the single press must still be pending: a double-press
        // action IS configured, so the detector must wait out the window rather than dispatch now.
        Assert.Empty(Snapshot(_engine.ToggleMuteCalls));

        Assert.True(WaitUntil(() => _engine.ToggleMuteCalls.Count > 0, TimeSpan.FromSeconds(5)),
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
    public void MutePress_OwnChangeLandsUnmuted_RaisesMuteOsdWithRealLevel()
    {
        var runtime = StartRuntime();
        var osd = new Recorder<VolumeChangeNotification>();
        runtime.VolumeChanged += osd.Handler;
        _engine.DeviceStates[Speakers] = (0.42f, true);   // muted before the press

        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => _engine.ToggleMuteCalls.Count > 0), "press never executed");

        _engine.DeviceStates[Speakers] = (0.42f, false);
        _engine.RaiseDeviceVolumeChanged(Speakers, 0.42f, muted: false, isOwnChange: true);

        var n = Assert.Single(osd.Items);
        Assert.Equal(OsdKind.Mute, n.Kind);
        Assert.False(n.Muted);
        Assert.Equal(0.42f, n.Level);
    }

    [Fact]
    public void MutePress_OwnChangeLandsMuted_RaisesMuteOsdWithRealLevel()
    {
        var runtime = StartRuntime();
        var osd = new Recorder<VolumeChangeNotification>();
        runtime.VolumeChanged += osd.Handler;
        _engine.DeviceStates[Speakers] = (0.42f, false);

        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => _engine.ToggleMuteCalls.Count > 0), "press never executed");

        _engine.DeviceStates[Speakers] = (0.42f, true);
        _engine.RaiseDeviceVolumeChanged(Speakers, 0.42f, muted: true, isOwnChange: true);

        var n = Assert.Single(osd.Items);
        Assert.True(n.Muted);
        Assert.Equal(0.42f, n.Level);
    }

    [Fact]
    public void MutePress_MuteOsd_CarriesTheDeviceIcon()
    {
        _engine.Devices[0] = DeviceById(Speakers) with { FormFactor = DeviceFormFactor.Speakers };
        _engine.DefaultRender = DeviceById(Speakers);
        var runtime = StartRuntime(NewConfig(c =>
            c.Devices.Add(new DeviceSettingsConfig { Id = Microphone, Flow = AudioFlow.Capture, Icon = ScreenIcon.StudioMic })));
        var osd = new Recorder<VolumeChangeNotification>();
        runtime.VolumeChanged += osd.Handler;

        Press(Connection(0), 0);   // MuteCurrentOutput: icon from the form factor
        Assert.True(WaitUntil(() => _engine.ToggleMuteCalls.Count == 1), "output press never executed");
        _engine.RaiseDeviceVolumeChanged(Speakers, 0.5f, muted: true, isOwnChange: true);
        Assert.True(WaitUntil(() => osd.Count == 1), "no output mute OSD");

        Press(Connection(0), 1);   // MuteCurrentInput: the chosen icon
        Assert.True(WaitUntil(() => _engine.ToggleMuteCalls.Count == 2), "input press never executed");
        _engine.RaiseDeviceVolumeChanged(Microphone, 0.5f, muted: true, isOwnChange: true);
        Assert.True(WaitUntil(() => osd.Count == 2), "no input mute OSD");

        var items = osd.Items;
        Assert.Equal(ScreenIcon.Speaker, items[0].Icon);
        Assert.Equal(ScreenIcon.StudioMic, items[1].Icon);
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
        var config = NewConfig(c => c.Buttons[0].Default().SinglePress = new ButtonActionConfig
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

    // ---- output-device switch OSD ----------------------------------------------------------

    private static ButtonActionConfig SetDevice(string id, string name = "") => new()
    {
        Action = ButtonActionType.SetOutputDevice,
        Device = new DeviceRefConfig { Id = id, FriendlyName = name },
    };

    private static ButtonActionConfig ToggleDevice() => new() { Action = ButtonActionType.ToggleOutputDevice };

    private AudioDevice DeviceById(string id) => _engine.Devices.First(d => d.Id == id);

    private (VolcoRuntime Runtime, Recorder<VolumeChangeNotification> Osd) StartWithButton0(ButtonActionConfig action)
    {
        var runtime = StartRuntime(NewConfig(c => c.Buttons[0].Default().SinglePress = action));
        var osd = new Recorder<VolumeChangeNotification>();
        runtime.VolumeChanged += osd.Handler;
        return (runtime, osd);
    }

    [Fact]
    public void SetOutputDevicePress_RaisesDeviceOsdOnlyAfterDefaultChanged_AndOnlyOnce()
    {
        _engine.DeviceStates[Headphones] = (0.3f, false);
        var (_, osd) = StartWithButton0(SetDevice(Headphones, "Headphones"));

        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => _engine.SetDefaultCalls.Count > 0), "press never executed");
        Assert.Equal(Headphones, Snapshot(_engine.SetDefaultCalls)[0].EndpointId);
        Assert.Empty(osd.Items);   // the switch has not landed yet

        // Windows raises DefaultChanged once per role.
        for (var i = 0; i < 3; i++)
            _engine.RaiseDeviceChanged(DeviceChangeKind.DefaultChanged, DeviceById(Headphones));

        var n = Assert.Single(osd.Items);
        Assert.Equal(OsdKind.Device, n.Kind);
        Assert.Equal("Headphones", n.Label);
        Assert.Equal(0.3f, n.Level);
        Assert.False(n.Muted);
    }

    [Fact]
    public void SetOutputDevicePress_DeviceOsd_CarriesTheFormFactorIcon()
    {
        _engine.Devices[1] = DeviceById(Headphones) with { FormFactor = DeviceFormFactor.Headphones };
        var (_, osd) = StartWithButton0(SetDevice(Headphones));

        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => _engine.SetDefaultCalls.Count > 0), "press never executed");
        _engine.RaiseDeviceChanged(DeviceChangeKind.DefaultChanged, DeviceById(Headphones));

        Assert.Equal(ScreenIcon.Headphones, Assert.Single(osd.Items).Icon);
    }

    [Fact]
    public void SetOutputDevicePress_DeviceOsd_CarriesTheChosenIconAndNickname()
    {
        _engine.Devices[1] = DeviceById(Headphones) with { FormFactor = DeviceFormFactor.Headphones };
        var runtime = StartRuntime(NewConfig(c =>
        {
            c.Buttons[0].Default().SinglePress = SetDevice(Headphones);
            c.Devices.Add(new DeviceSettingsConfig { Id = Headphones, Nickname = "Desk", Icon = ScreenIcon.Earbuds });
        }));
        var osd = new Recorder<VolumeChangeNotification>();
        runtime.VolumeChanged += osd.Handler;

        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => _engine.SetDefaultCalls.Count > 0), "press never executed");
        _engine.RaiseDeviceChanged(DeviceChangeKind.DefaultChanged, DeviceById(Headphones));

        var n = Assert.Single(osd.Items);
        Assert.Equal("Desk", n.Label);
        Assert.Equal(ScreenIcon.Earbuds, n.Icon);
    }

    [Fact]
    public void SetOutputDevicePress_TargetMuted_CarriesMuteFlag()
    {
        _engine.DeviceStates[Headphones] = (0.3f, true);
        var (_, osd) = StartWithButton0(SetDevice(Headphones));

        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => _engine.SetDefaultCalls.Count > 0), "press never executed");
        _engine.RaiseDeviceChanged(DeviceChangeKind.DefaultChanged, DeviceById(Headphones));

        var n = Assert.Single(osd.Items);
        Assert.True(n.Muted);
        Assert.Equal(0.3f, n.Level);
    }

    [Fact]
    public void SetOutputDevicePress_StateUnreadable_StillShowsNameAtLevelZero()
    {
        _engine.DeviceStates.Remove(Headphones);
        var (_, osd) = StartWithButton0(SetDevice(Headphones));

        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => _engine.SetDefaultCalls.Count > 0), "press never executed");
        _engine.RaiseDeviceChanged(DeviceChangeKind.DefaultChanged, DeviceById(Headphones));

        var n = Assert.Single(osd.Items);
        Assert.Equal("Headphones", n.Label);
        Assert.Equal(0f, n.Level);
        Assert.False(n.Muted);
    }

    [Fact]
    public void ToggleOutputDevicePress_OnDevice1_TargetsDevice2()
    {
        _engine.DeviceStates[Headphones] = (0.6f, false);
        var (_, osd) = StartWithButton0(ToggleDevice());   // default render is Speakers (Device1)

        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => _engine.SetDefaultCalls.Count > 0), "press never executed");
        Assert.Equal(Headphones, Snapshot(_engine.SetDefaultCalls)[0].EndpointId);
        Assert.Empty(osd.Items);

        _engine.RaiseDeviceChanged(DeviceChangeKind.DefaultChanged, DeviceById(Headphones));

        var n = Assert.Single(osd.Items);
        Assert.Equal(OsdKind.Device, n.Kind);
        Assert.Equal("Headphones", n.Label);
        Assert.Equal(0.6f, n.Level);
    }

    [Fact]
    public void ToggleOutputDevicePress_OnAnotherDevice_TargetsDevice1()
    {
        _engine.DefaultRender = DeviceById(Headphones);   // Device2 is current -> back to Device1
        var (_, osd) = StartWithButton0(ToggleDevice());

        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => _engine.SetDefaultCalls.Count > 0), "press never executed");
        Assert.Equal(Speakers, Snapshot(_engine.SetDefaultCalls)[0].EndpointId);

        _engine.RaiseDeviceChanged(DeviceChangeKind.DefaultChanged, DeviceById(Speakers));

        Assert.Equal("Speakers", Assert.Single(osd.Items).Label);
    }

    [Fact]
    public void SetOutputDevicePress_TargetAlreadyDefault_RaisesDeviceOsdImmediately()
    {
        _engine.DeviceStates[Speakers] = (0.8f, false);
        var (_, osd) = StartWithButton0(SetDevice(Speakers, "Speakers"));   // Speakers is already default

        Press(Connection(0), 0);

        Assert.True(WaitUntil(() => osd.Count > 0), "no confirmation OSD for a no-op switch");
        var n = Assert.Single(osd.Items);
        Assert.Equal(OsdKind.Device, n.Kind);
        Assert.Equal("Speakers", n.Label);
        Assert.Equal(0.8f, n.Level);

        // Nothing is left pending: a later DefaultChanged to the same device stays silent.
        _engine.RaiseDeviceChanged(DeviceChangeKind.DefaultChanged, DeviceById(Speakers));
        Assert.Single(osd.Items);
    }

    [Fact]
    public void DefaultChanged_ThatDoesNotMatchPendingTarget_RaisesNoOsd()
    {
        var (_, osd) = StartWithButton0(SetDevice(Headphones, "Headphones"));

        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => _engine.SetDefaultCalls.Count > 0), "press never executed");

        // Different device, and the right id on the wrong flow.
        _engine.RaiseDeviceChanged(DeviceChangeKind.DefaultChanged, DeviceById(Speakers));
        _engine.RaiseDeviceChanged(DeviceChangeKind.DefaultChanged,
            new AudioDevice(Headphones, "Headphones", AudioFlow.Capture, false));
        // Right device, wrong change kind.
        _engine.RaiseDeviceChanged(DeviceChangeKind.StateChanged, DeviceById(Headphones));
        Assert.Empty(osd.Items);

        // The same pending record is still live: the matching event now produces the OSD.
        _engine.RaiseDeviceChanged(DeviceChangeKind.DefaultChanged, DeviceById(Headphones));
        var n = Assert.Single(osd.Items);
        Assert.Equal(OsdKind.Device, n.Kind);
        Assert.Equal("Headphones", n.Label);
    }

    [Fact]
    public void ExternalDefaultChanged_WithNoPress_RaisesNoOsd()
    {
        var (_, osd) = StartWithButton0(SetDevice(Headphones, "Headphones"));

        _engine.RaiseDeviceChanged(DeviceChangeKind.DefaultChanged, DeviceById(Headphones));
        Assert.Empty(osd.Items);

        // Prove the path can fire: the same event after a press does.
        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => _engine.SetDefaultCalls.Count > 0), "press never executed");
        _engine.RaiseDeviceChanged(DeviceChangeKind.DefaultChanged, DeviceById(Headphones));
        Assert.Single(osd.Items);
    }

    [Fact]
    public void SetOutputDevicePress_WithEmptyTargetId_RaisesNoOsd()
    {
        var config = NewConfig(c =>
        {
            c.Buttons[0].Default().SinglePress = SetDevice("");   // unconfigured (no id, no name, so HealDeviceIds cannot fill it): a no-op
            c.Buttons[2].Default().SinglePress = SetDevice(Headphones, "Headphones");
        });
        var runtime = StartRuntime(config);
        var osd = new Recorder<VolumeChangeNotification>();
        runtime.VolumeChanged += osd.Handler;

        Press(Connection(0), 0);
        Assert.True(StaysUnchanged(() => osd.Count));
        Assert.Empty(Snapshot(_engine.SetDefaultCalls));
        _engine.RaiseDeviceChanged(DeviceChangeKind.DefaultChanged, DeviceById(Headphones));
        Assert.Empty(osd.Items);

        // Prove the path can fire with a real target.
        Press(Connection(0), 2);
        Assert.True(WaitUntil(() => _engine.SetDefaultCalls.Count > 0), "press never executed");
        _engine.RaiseDeviceChanged(DeviceChangeKind.DefaultChanged, DeviceById(Headphones));
        Assert.Single(osd.Items);
    }

    [Fact]
    public void LaterPress_ReplacesPendingSwitch()
    {
        var config = NewConfig(c =>
        {
            c.Buttons[0].Default().SinglePress = SetDevice(Headphones, "Headphones");
            c.Buttons[2].Default().SinglePress = new ButtonActionConfig { Action = ButtonActionType.MuteCurrentInput };
        });
        var runtime = StartRuntime(config);
        var osd = new Recorder<VolumeChangeNotification>();
        runtime.VolumeChanged += osd.Handler;

        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => _engine.SetDefaultCalls.Count > 0), "press never executed");
        Press(Connection(0), 2);
        Assert.True(WaitUntil(() => _engine.ToggleMuteCalls.Count > 0), "second press never executed");

        _engine.RaiseDeviceChanged(DeviceChangeKind.DefaultChanged, DeviceById(Headphones));
        Assert.Empty(osd.Items);

        // Prove the path can fire: a fresh switch press works after the replaced one.
        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => _engine.SetDefaultCalls.Count > 1), "third press never executed");
        _engine.RaiseDeviceChanged(DeviceChangeKind.DefaultChanged, DeviceById(Headphones));
        Assert.Equal(OsdKind.Device, Assert.Single(osd.Items).Kind);
    }

    [Fact]
    public void SetOutputDevicePress_TargetNotPresent_RaisesNoOsdEvenIfItLaterBecomesDefault()
    {
        var config = NewConfig(c =>
        {
            c.Buttons[0].Default().SinglePress = SetDevice("absent-id", "Unplugged Headset");
            c.Buttons[2].Default().SinglePress = SetDevice(Headphones, "Headphones");
        });
        var runtime = StartRuntime(config);
        var osd = new Recorder<VolumeChangeNotification>();
        runtime.VolumeChanged += osd.Handler;

        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => _engine.SetDefaultCalls.Count > 0), "press never executed");

        // The headset is plugged in later and becomes default by some other means.
        var headset = new AudioDevice("absent-id", "Unplugged Headset", AudioFlow.Render, false);
        _engine.Devices.Add(headset);
        _engine.RaiseDeviceChanged(DeviceChangeKind.DefaultChanged, headset);
        Assert.Empty(osd.Items);

        // Prove the path can fire with a present target.
        Press(Connection(0), 2);
        Assert.True(WaitUntil(() => _engine.SetDefaultCalls.Count > 1), "second press never executed");
        _engine.RaiseDeviceChanged(DeviceChangeKind.DefaultChanged, DeviceById(Headphones));
        Assert.Single(osd.Items);
    }

    [Fact]
    public void SetOutputDevicePress_SwitchNeverLands_ExpiresAndRaisesNoOsd()
    {
        var options = Quiet with { PendingSwitchTimeout = TimeSpan.FromMilliseconds(100) };
        var runtime = StartRuntime(NewConfig(c => c.Buttons[0].Default().SinglePress = SetDevice(Headphones, "Headphones")),
                                   options: options);
        var osd = new Recorder<VolumeChangeNotification>();
        runtime.VolumeChanged += osd.Handler;

        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => _engine.SetDefaultCalls.Count > 0), "press never executed");
        Thread.Sleep(250);   // past the 100 ms deadline

        _engine.RaiseDeviceChanged(DeviceChangeKind.DefaultChanged, DeviceById(Headphones));
        Assert.Empty(osd.Items);

        // Prove the path can fire: a fresh press inside the window does.
        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => _engine.SetDefaultCalls.Count > 1), "second press never executed");
        _engine.RaiseDeviceChanged(DeviceChangeKind.DefaultChanged, DeviceById(Headphones));
        Assert.Single(osd.Items);
    }

    // ---- controller-state pushes -----------------------------------------------------------

    [Fact]
    public void Hello_PushesStateForEveryButton()
    {
        var runtime = StartRuntime();
        var statuses = new Recorder<TransportStatus>();
        runtime.TransportStatusChanged += statuses.Handler;
        Assert.Equal(TransportStatus.Closed, runtime.TransportStatus);

        ConnectAndGreet(Connection(0));

        var frames = Connection(0).SentFrames;
        Assert.True(ContainsFrame(frames, ProtocolEncoder.EncodeHelloRequest()), "no HelloRequest on connect");
        Assert.True(ContainsFrame(frames, ProtocolEncoder.EncodeLed(0, 0)), "button 0 feedback frame missing");
        Assert.True(ContainsFrame(frames, ProtocolEncoder.EncodeLed(1, 0)), "button 1 feedback frame missing");
        Assert.True(ContainsFrame(frames, ProtocolEncoder.EncodeLed(2, 0)), "button 2 feedback frame missing");
        Assert.Equal(TransportStatus.Open, runtime.TransportStatus);
        Assert.Equal(new[] { TransportStatus.Open }, statuses.Items);
    }

    [Fact]
    public void OwnVolumeChanges_DoNotResendIdenticalFrames()
    {
        StartRuntime();
        var connection = Connection(0);
        ConnectAndGreet(connection);
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
        ConnectAndGreet(connection);
        var baseline = connection.SentCount;

        _engine.DeviceStates[Speakers] = (0.5f, true);
        _engine.RaiseDeviceVolumeChanged(Speakers, 0.5f, muted: true, isOwnChange: false);

        Assert.True(connection.SentCount > baseline, "mute flip did not resend frames");
        var newFrames = connection.SentFrames.Skip(baseline);
        Assert.True(ContainsFrame(newFrames, ProtocolEncoder.EncodeLed(0, 1)), "muted frame not sent");
    }

    [Fact]
    public void Connected_SendsOnlyHelloRequest_UntilHello()
    {
        StartRuntime();
        var connection = Connection(0);
        connection.SetStatus(TransportStatus.Open);
        _engine.DeviceStates[Speakers] = (0.5f, true);
        _engine.RaiseDeviceVolumeChanged(Speakers, 0.5f, muted: true, isOwnChange: false);

        Assert.Equal(new[] { ProtocolEncoder.EncodeHelloRequest() }, connection.SentFrames);
    }

    [Fact]
    public void Hello_StoresControllerInfo_AndRaisesTheEvents()
    {
        var config = NewConfig();
        var runtime = StartRuntime(config);
        var states = new Recorder<ControllerState>();
        var infos = 0;
        runtime.ControllerStateChanged += states.Handler;
        runtime.ControllerInfoChanged += (_, _) => Interlocked.Increment(ref infos);

        ConnectAndGreet(Connection(0));

        Assert.Equal(ControllerState.Ready, runtime.ControllerState);
        Assert.Equal(new[] { ControllerState.AwaitingHello, ControllerState.Ready }, states.Items);
        Assert.Equal(new[] { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair }, config.Config.Controller!.Buttons);
        Assert.Equal(1, infos);

        Connection(0).RaiseMessage(LedHello);         // same hardware again: no new info event
        Assert.Equal(1, infos);
    }

    // ---- test mode (spec §8.5) -----------------------------------------------------------------

    private static byte[]? LastLed(FakeControllerConnection c, int button)
        => c.SentFrames.LastOrDefault(f => f.Length == 6 && f[2] == 0x83 && f[3] == button);

    [Fact]
    public void TestMode_SliderAndButtonMessages_RaiseRawInput_AndReachNothingElse()
    {
        var runtime = StartRuntime();
        var c = Connection(0);
        ConnectAndGreet(c);
        var raw = new ConcurrentQueue<RawInputEventArgs>();
        runtime.RawInput += (_, e) => raw.Enqueue(e);
        var activity = 0;
        runtime.ButtonActivity += (_, _) => activity++;

        using (runtime.EnterTestMode())
        {
            Assert.True(runtime.InTestMode);
            c.RaiseMessage(new SliderMessage(2, 1234));
            Press(c, 0);
            Thread.Sleep(100);
        }

        Assert.Equal(new[] { (RawInputKind.Slider, 2, 1234), (RawInputKind.Button, 0, 1), (RawInputKind.Button, 0, 0) },
                     raw.Select(e => (e.Kind, e.Index, e.Value)).ToArray());
        Assert.Empty(Snapshot(_engine.DeviceVolumeCalls));
        Assert.Empty(Snapshot(_engine.ToggleMuteCalls));
        Assert.Equal(0, activity);
        Assert.False(runtime.InTestMode);
    }

    [Fact]
    public void RawInputIsRaisedOutsideTestMode_AndInputStillWorks()
    {
        var runtime = StartRuntime();
        var c = Connection(0);
        ConnectAndGreet(c);
        var raw = new ConcurrentQueue<RawInputEventArgs>();
        runtime.RawInput += (_, e) => raw.Enqueue(e);
        c.RaiseMessage(new SliderMessage(0, 100));
        Press(c, 0);
        Assert.Equal(new[] { (RawInputKind.Slider, 0, 100), (RawInputKind.Button, 0, 1), (RawInputKind.Button, 0, 0) },
                     raw.Select(e => (e.Kind, e.Index, e.Value)).ToArray());
        Assert.True(WaitUntil(() => Snapshot(_engine.ToggleMuteCalls).Count == 1));
    }

    [Fact]
    public void TestMode_AButtonHeldAcrossEntry_FiresNoLongPress()
    {
        var config = NewConfig(c =>
        {
            c.Buttons[0].Default().LongPress = new ButtonActionConfig { Action = ButtonActionType.MuteCurrentInput };
            c.Buttons[0].LongPressMs = 150;
        });
        var runtime = StartRuntime(config);
        var c = Connection(0);
        ConnectAndGreet(c);
        c.RaiseMessage(new ButtonMessage(0, ButtonState.Pressed));
        using (runtime.EnterTestMode())
        {
            Thread.Sleep(400);
            c.RaiseMessage(new ButtonMessage(0, ButtonState.Released));
        }
        Assert.True(StaysUnchanged(() => Snapshot(_engine.ToggleMuteCalls).Count));
        Assert.Empty(Snapshot(_engine.ToggleMuteCalls));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(80)]
    public void TestMode_AButtonHeldAcrossExit_ItsReleaseFiresNothing(int doublePressWindowMs)
    {
        var config = NewConfig(c =>
        {
            c.Buttons[0].DoublePressWindowMs = doublePressWindowMs;
            if (doublePressWindowMs > 0)
                c.Buttons[0].Default().DoublePress = new ButtonActionConfig { Action = ButtonActionType.MuteCurrentInput };
        });
        var runtime = StartRuntime(config);
        var c = Connection(0);
        ConnectAndGreet(c);
        var test = runtime.EnterTestMode();
        c.RaiseMessage(new ButtonMessage(0, ButtonState.Pressed));
        test.Dispose();
        c.RaiseMessage(new ButtonMessage(0, ButtonState.Released));
        Assert.True(StaysUnchanged(() => Snapshot(_engine.ToggleMuteCalls).Count));
        Assert.Empty(Snapshot(_engine.ToggleMuteCalls));
    }

    [Fact]
    public void TestMode_PressesToggleTheLeds_ThenExitRestoresTheRealState()
    {
        var runtime = StartRuntime();
        var c = Connection(0);
        ConnectAndGreet(c);   // LED, LED, LED pair
        var real0 = LastLed(c, 0)!;
        var real2 = LastLed(c, 2)!;

        var test = runtime.EnterTestMode();
        Press(c, 0);
        Assert.Equal(ProtocolEncoder.EncodeLed(0, 1), LastLed(c, 0));
        Press(c, 0);
        Assert.Equal(ProtocolEncoder.EncodeLed(0, 0), LastLed(c, 0));   // a single LED toggles
        Press(c, 2);
        Assert.Equal(ProtocolEncoder.EncodeLed(2, 1), LastLed(c, 2));   // a pair: A …
        Press(c, 2);
        Assert.Equal(ProtocolEncoder.EncodeLed(2, 2), LastLed(c, 2));   // … then B …
        Press(c, 2);
        Assert.Equal(ProtocolEncoder.EncodeLed(2, 1), LastLed(c, 2));   // … then A again

        test.Dispose();
        Assert.Equal(real0, LastLed(c, 0));
        Assert.Equal(real2, LastLed(c, 2));
    }

    [Fact]
    public void AdvanceTestIndicatorMovesLikeAPress()
    {
        var runtime = StartRuntime();
        var c = Connection(0);
        ConnectAndGreet(c, new HelloMessage(ProtocolVersion.Current, 5,
                                            new[] { IndicatorKind.Led, IndicatorKind.LedPair, IndicatorKind.Screen }));
        var activity = 0;
        runtime.ButtonActivity += (_, _) => activity++;
        Assert.Equal(0, runtime.AdvanceTestIndicator(0));   // no test: nothing moves

        using (runtime.EnterTestMode())
        {
            Assert.Equal(1, runtime.AdvanceTestIndicator(0));
            Assert.Equal(ProtocolEncoder.EncodeLed(0, 1), LastLed(c, 0));   // a single LED: On …
            Assert.Equal(2, runtime.AdvanceTestIndicator(0));
            Assert.Equal(ProtocolEncoder.EncodeLed(0, 0), LastLed(c, 0));   // … then Off

            runtime.AdvanceTestIndicator(1);
            Assert.Equal(ProtocolEncoder.EncodeLed(1, 1), LastLed(c, 1));   // a pair: A …
            runtime.AdvanceTestIndicator(1);
            Assert.Equal(ProtocolEncoder.EncodeLed(1, 2), LastLed(c, 1));   // … B …
            runtime.AdvanceTestIndicator(1);
            Assert.Equal(ProtocolEncoder.EncodeLed(1, 1), LastLed(c, 1));   // … A

            Assert.Equal(1, runtime.AdvanceTestIndicator(2));
            Assert.True(ContainsFrame(c.SentFrames, ProtocolEncoder.EncodeScreen(2, ScreenTransition.None,
                ScreenContent.Text("Button 3", ScreenIcon.Generic, false, "1 press"))));
            Press(c, 2);   // a real press counts on from there
            Assert.True(ContainsFrame(c.SentFrames, ProtocolEncoder.EncodeScreen(2, ScreenTransition.None,
                ScreenContent.Text("Button 3", ScreenIcon.Generic, false, "2 presses"))));
        }
        Assert.Equal(0, activity);
        Assert.Empty(Snapshot(_engine.ToggleMuteCalls));
        Assert.Empty(Snapshot(_engine.DeviceVolumeCalls));
    }

    [Fact]
    public void TestMode_RawInput_CarriesThePressCount_AndANewTestStartsAgain()
    {
        var runtime = StartRuntime();
        var c = Connection(0);
        ConnectAndGreet(c);
        var raw = new ConcurrentQueue<RawInputEventArgs>();
        runtime.RawInput += (_, e) => raw.Enqueue(e);

        using (runtime.EnterTestMode())
        {
            Press(c, 1);
            Press(c, 1);
        }
        Assert.Equal(new[] { (1, 1), (0, 1), (1, 2), (0, 2) }, raw.Select(e => (e.Value, e.Presses)).ToArray());

        raw.Clear();
        using (runtime.EnterTestMode()) Press(c, 1);
        Assert.Equal(1, raw.First().Presses);
    }

    [Fact]
    public void TestMode_ScreenButtons_ShowTheTestScreen_ThenCountPresses()
    {
        var runtime = StartRuntime();
        var c = Connection(0);
        ConnectAndGreet(c, new HelloMessage(ProtocolVersion.Current, 5, new[] { IndicatorKind.Screen, IndicatorKind.Screen }));
        using (runtime.EnterTestMode())
        {
            var expected = ProtocolEncoder.EncodeScreen(1, ScreenTransition.None,
                ScreenContent.Text("Test", ScreenIcon.Generic, false, "Button 2"));   // input 1, numbered from 1 as in the UI
            Assert.True(ContainsFrame(c.SentFrames, expected));

            Press(c, 1);
            Press(c, 1);
            Assert.True(ContainsFrame(c.SentFrames, ProtocolEncoder.EncodeScreen(1, ScreenTransition.None,
                ScreenContent.Text("Button 2", ScreenIcon.Generic, false, "2 presses"))));
        }
    }

    [Fact]
    public void TestMode_EndsOnDetach_Reconnect_AndDispose()
    {
        var runtime = StartRuntime();
        var c = Connection(0);
        ConnectAndGreet(c);
        var changes = new ConcurrentQueue<bool>();
        runtime.InTestModeChanged += (_, on) => changes.Enqueue(on);

        runtime.EnterTestMode();
        c.SetStatus(TransportStatus.Closed);
        Assert.False(runtime.InTestMode);

        c.SetStatus(TransportStatus.Open);
        runtime.EnterTestMode();
        runtime.Reconnect();
        Assert.False(runtime.InTestMode);

        runtime.EnterTestMode();
        runtime.Dispose();
        Assert.False(runtime.InTestMode);
        Assert.Equal(new[] { true, false, true, false, true, false }, changes.ToArray());
    }

    [Fact]
    public void TestMode_AStaleHandle_DoesNotEndANewerSession()
    {
        var runtime = StartRuntime();
        ConnectAndGreet(Connection(0));
        var first = runtime.EnterTestMode();
        first.Dispose();
        var second = runtime.EnterTestMode();
        first.Dispose();
        Assert.True(runtime.InTestMode);
        second.Dispose();
        Assert.False(runtime.InTestMode);
    }

    // ---- config session plumbing (spec §8.2) --------------------------------------------------

    private static readonly byte[] SmallBlob = { 0x32, 0x02, 0x08, 0x64 };

    private static HelloMessage StoredHello(uint crc) => new(ProtocolVersion.Current, ChipFamily.Esp32, "dev",
        ControllerConfigState.Stored, crc, 5, new[] { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair });

    private static byte LastWriteSeq(FakeControllerConnection c) => c.SentFrames.Last(f => f.Length > 3 && f[2] == 0x88)[3];

    private static async Task<T> Within<T>(Task<T> task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(WaitTimeout * 2)));
        return await task;
    }

    [Fact]
    public void Hello_ReachesTheConfigService_AfterTheLink()
    {
        var runtime = StartRuntime();
        ConnectAndGreet(Connection(0));
        Assert.Same(LedHello, runtime.ControllerConfig.LastHello);
        Assert.Equal(BannerKind.NotSetUp, runtime.ControllerConfig.Banner.Kind);
    }

    [Fact]
    public async Task Save_DetachAndReattachOfTheSameConnectionWhileConfirming_StillConfirmed()
    {
        var runtime = StartRuntime();
        var c = Connection(0);
        ConnectAndGreet(c);
        var save = runtime.ControllerConfig.SaveAsync(SmallBlob);
        c.RaiseMessage(new ConfigResultMessage(LastWriteSeq(c), ConfigResultCode.Ok, 0xFF, 0xFF));
        Assert.True(WaitUntil(() => runtime.ControllerConfig.Progress?.Phase == SavePhase.Confirming));

        c.SetStatus(TransportStatus.Closed);   // the reboot drops the transport for a moment
        c.SetStatus(TransportStatus.Open);
        c.RaiseMessage(StoredHello(ControllerConfigCodec.Crc32(SmallBlob)));

        Assert.Equal(SaveStatus.Confirmed, (await Within(save)).Status);
        Assert.Equal(Convert.ToBase64String(SmallBlob), runtime.Config.Config.ControllerConfigBlob);
    }

    [Fact]
    public async Task Save_ReconnectMidSave_ConnectionLost()
    {
        var runtime = StartRuntime();
        ConnectAndGreet(Connection(0));
        var save = runtime.ControllerConfig.SaveAsync(SmallBlob);
        runtime.Reconnect();
        Assert.Equal(SaveStatus.ConnectionLost, (await Within(save)).Status);
        Assert.Null(runtime.Config.Config.ControllerConfigBlob);
    }

    [Fact]
    public async Task ConfigMessages_FromARetiredConnection_AreDropped()
    {
        // Fake time: the 3 s result wait can't expire on a slow runner and change the seq.
        var runtime = StartRuntime(options: Quiet with { Time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider() });
        var old = Connection(0);
        ConnectAndGreet(old);
        runtime.Reconnect();
        var current = Connection(1);
        ConnectAndGreet(current);

        var save = runtime.ControllerConfig.SaveAsync(SmallBlob);
        var seq = LastWriteSeq(current);
        old.RaiseMessage(new ConfigResultMessage(seq, ConfigResultCode.Invalid, 0, 1));   // retired: ignored
        old.RaiseMessage(StoredHello(ControllerConfigCodec.Crc32(SmallBlob)));
        await Task.Delay(100);
        Assert.False(save.IsCompleted);

        current.RaiseMessage(new ConfigResultMessage(seq, ConfigResultCode.Invalid, 0, 1));
        Assert.Equal(SaveStatus.Rejected, (await Within(save)).Status);
    }

    [Fact]
    public void ConfigSession_SendsNothing_ForATestHelloWithNoStoredConfig()
    {
        var runtime = StartRuntime();
        var c = Connection(0);
        ConnectAndGreet(c);
        Thread.Sleep(100);
        Assert.DoesNotContain(c.SentFrames, f => f.Length > 2 && f[2] is 0x87 or 0x88);
        Assert.Equal(ConfigOperation.Idle, runtime.ControllerConfig.Operation);
    }

    [Fact]
    public void Hello_WrongVersion_IsAMismatch_AndInputStillWorks()
    {
        var runtime = StartRuntime();
        var connection = Connection(0);
        ConnectAndGreet(connection, new HelloMessage(42, 5, new[] { IndicatorKind.Led }));
        var after = connection.SentCount;

        Press(connection, 0);

        Assert.Equal(ControllerState.VersionMismatch, runtime.ControllerState);
        Assert.True(WaitUntil(() => _engine.ToggleMuteCalls.Count > 0), "button input ignored during a mismatch");
        Assert.Equal(after, connection.SentCount);
    }

    [Fact]
    public void Reconnect_WithoutControllerReboot_RequestsHelloAndPushesEverything()
    {
        // A new session on a controller that never rebooted.
        var runtime = StartRuntime();
        ConnectAndGreet(Connection(0));
        runtime.Reconnect();
        var fresh = Connection(1);

        fresh.SetStatus(TransportStatus.Open);
        Assert.Equal(new[] { ProtocolEncoder.EncodeHelloRequest() }, fresh.SentFrames);
        fresh.RaiseMessage(LedHello);

        Assert.True(ContainsFrame(fresh.SentFrames, ProtocolEncoder.EncodeLed(0, 0)));
        Assert.True(ContainsFrame(fresh.SentFrames, ProtocolEncoder.EncodeLed(2, 0)));
    }

    [Fact]
    public void Disconnect_RetiresTheTransport_AndReportsDisconnected()
    {
        var runtime = StartRuntime();
        ConnectAndGreet(Connection(0));
        var statuses = new Recorder<TransportStatus>();
        var flags = new Recorder<bool>();
        runtime.TransportStatusChanged += statuses.Handler;
        runtime.UserDisconnectedChanged += flags.Handler;

        runtime.Disconnect();

        Assert.True(runtime.IsUserDisconnected);
        Assert.Equal(1, Connection(0).DisposeCount);
        Assert.Equal(TransportStatus.Closed, runtime.TransportStatus);
        Assert.Equal(new[] { TransportStatus.Closed }, statuses.Items);
        Assert.Equal(new[] { true }, flags.Items);
        Assert.Equal(1, ConnectionCount);

        runtime.Disconnect(); // already disconnected: nothing more
        Assert.Single(flags.Items);
    }

    [Fact]
    public void Disconnected_ConnectionConfigChange_StartsNoTransport_UntilConnect()
    {
        var config = NewConfig();
        var runtime = StartRuntime(config);
        runtime.Disconnect();

        var changes = 0;
        config.ConnectionConfigChanged += (_, _) => Interlocked.Increment(ref changes);
        config.Config.Connection.Udp.ListenPort = 23456;
        config.RequestSave(connectionChanged: true);
        Assert.True(WaitUntil(() => Volatile.Read(ref changes) > 0), "the debounced save never ran");
        Assert.Equal(1, ConnectionCount);

        var flags = new Recorder<bool>();
        runtime.UserDisconnectedChanged += flags.Handler;
        runtime.Connect();

        Assert.False(runtime.IsUserDisconnected);
        Assert.Equal(new[] { false }, flags.Items);
        Assert.Equal(2, ConnectionCount);
        Assert.Equal(1, Connection(1).ConnectCount);
        Assert.Equal(23456, Connection(1).Config.Udp.ListenPort);

        runtime.Connect(); // already connected: no second transport
        Assert.Equal(2, ConnectionCount);
    }

    [Fact]
    public void Reconnect_EndsADisconnect()
    {
        var runtime = StartRuntime();
        runtime.Disconnect();

        runtime.Reconnect();

        Assert.False(runtime.IsUserDisconnected);
        Assert.Equal(2, ConnectionCount);
        Assert.Equal(1, Connection(1).ConnectCount);
    }

    [Fact]
    public void NextMode_SlidesLeft_EvenWhenWrapping()
    {
        var config = TwoModeConfig();
        config.Config.Controller = new ControllerInfoConfig { ProtocolVersion = 1, Sliders = 5, Buttons = { IndicatorKind.Screen } };
        var runtime = StartRuntime(config);
        var connection = Connection(0);
        ConnectAndGreet(connection, new HelloMessage(ProtocolVersion.Current, 5, new[] { IndicatorKind.Screen }));

        runtime.SetActiveMode("gaming");
        var baseline = connection.SentCount;
        runtime.NextMode();                                     // gaming → default wraps

        var screen = connection.SentFrames.Skip(baseline).First(f => f[2] == (byte)MessageType.Screen);
        Assert.Equal((byte)ScreenTransition.SlideLeft, screen[4]);
    }

    [Fact]
    public void GoToAnEarlierMode_SlidesRight()
    {
        var config = TwoModeConfig();
        var runtime = StartRuntime(config);
        var connection = Connection(0);
        ConnectAndGreet(connection, new HelloMessage(ProtocolVersion.Current, 5, new[] { IndicatorKind.Screen }));
        runtime.SetActiveMode("gaming");
        var baseline = connection.SentCount;

        runtime.SetActiveMode(AppConfig.DefaultModeId);

        var screen = connection.SentFrames.Skip(baseline).First(f => f[2] == (byte)MessageType.Screen);
        Assert.Equal((byte)ScreenTransition.SlideRight, screen[4]);
    }

    [Fact]
    public void Unsynchronized_ModeChange_HasNoBeginOrCommit()
    {
        var runtime = StartRuntime(TwoModeConfig(), options: Quiet with { SynchronizedModeTransitions = false });
        var connection = Connection(0);
        ConnectAndGreet(connection);
        var baseline = connection.SentCount;

        runtime.SetActiveMode("gaming");

        var burst = connection.SentFrames.Skip(baseline).ToList();
        Assert.NotEmpty(burst);
        Assert.DoesNotContain(burst, f => f[2] is (byte)MessageType.Begin or (byte)MessageType.Commit);
    }

    [Fact]
    public void DeletingTheLiveMode_PushesInstantly()
    {
        var config = TwoModeConfig();
        var runtime = StartRuntime(config);
        var connection = Connection(0);
        ConnectAndGreet(connection, new HelloMessage(ProtocolVersion.Current, 5, new[] { IndicatorKind.Screen }));
        runtime.SetActiveMode("gaming");
        var baseline = connection.SentCount;

        config.Config.Modes.RemoveAll(p => p.Id == "gaming");
        runtime.SetActiveMode(AppConfig.DefaultModeId);       // what ButtonsViewModel does

        var screen = connection.SentFrames.Skip(baseline).First(f => f[2] == (byte)MessageType.Screen);
        Assert.Equal((byte)ScreenTransition.None, screen[4]);
        Assert.DoesNotContain(connection.SentFrames.Skip(baseline), f => f[2] == (byte)MessageType.Begin);
    }

    [Fact]
    public void DeletingTheFallbackLiveMode_NeverSetExplicitly_PushesInstantly()
    {
        // No SetActiveMode before: the live mode is the first one only by fallback.
        var config = TwoModeConfig();
        var runtime = StartRuntime(config);
        var connection = Connection(0);
        ConnectAndGreet(connection, new HelloMessage(ProtocolVersion.Current, 5, new[] { IndicatorKind.Screen }));
        var baseline = connection.SentCount;

        config.Config.Modes.RemoveAll(p => p.Id == AppConfig.DefaultModeId);
        runtime.SetActiveMode("gaming");                        // what ButtonsViewModel does

        var burst = connection.SentFrames.Skip(baseline).ToList();
        var screen = burst.First(f => f[2] == (byte)MessageType.Screen);
        Assert.Equal((byte)ScreenTransition.None, screen[4]);
        Assert.DoesNotContain(burst, f => f[2] == (byte)MessageType.Begin);
    }

    [Fact]
    public void ConfigChanged_PushesActionEdits()
    {
        var config = NewConfig();
        StartRuntime(config);
        var connection = Connection(0);
        ConnectAndGreet(connection);
        _engine.DeviceStates[Speakers] = (0.5f, true);            // muted, but button 2 does nothing yet
        var baseline = connection.SentCount;

        config.Config.Buttons[2].Default().SinglePress = new ButtonActionConfig { Action = ButtonActionType.MuteCurrentOutput };
        config.Save();                                            // raises ConfigChanged

        Assert.True(WaitUntil(() => ContainsFrame(connection.SentFrames.Skip(baseline), ProtocolEncoder.EncodeLed(2, 1))));
    }

    [Fact]
    public void DevicesChanged_RefreshesAStoredFriendlyName()
    {
        var config = NewConfig(c => c.Devices.Add(new DeviceSettingsConfig { Id = Speakers, FriendlyName = "Old name", Nickname = "Desk" }));
        StartRuntime(config);
        var renamed = new AudioDevice(Speakers, "Speakers (new driver)", AudioFlow.Render, true);
        _engine.Devices[0] = renamed;

        _engine.RaiseDeviceChanged(DeviceChangeKind.StateChanged, renamed);

        Assert.Equal("Speakers (new driver)", config.Config.FindDevice(Speakers)!.FriendlyName);
    }

    [Fact]
    public void DeviceRenamedInWindows_SliderAndActionNamesFollow_BeforeTheUiHears()
    {
        var config = NewConfig(c =>
        {
            c.Sliders[0].Type = SliderMappingType.Device;
            c.Sliders[0].Device = new DeviceRefConfig { Id = Speakers, FriendlyName = "Old name" };
            c.Buttons[1].Default().SinglePress = new ButtonActionConfig
            {
                Action = ButtonActionType.ToggleOutputDevice,
                Device = new DeviceRefConfig { Id = Speakers, FriendlyName = "Old name" },
                DeviceB = new DeviceRefConfig { Id = "gone", FriendlyName = "Unplugged" },
            };
        });
        var runtime = StartRuntime(config);
        string? seenByUi = null;
        runtime.DevicesChanged += (_, _) => seenByUi = config.Config.Sliders[0].Device!.FriendlyName;
        var renamed = new AudioDevice(Speakers, "Desk (Realtek)", AudioFlow.Render, true);
        _engine.Devices[0] = renamed;

        _engine.RaiseDeviceChanged(DeviceChangeKind.StateChanged, renamed);

        Assert.Equal("Desk (Realtek)", config.Config.Sliders[0].Device!.FriendlyName);
        Assert.Equal("Desk (Realtek)", config.Config.Buttons[1].Default().SinglePress.Device!.FriendlyName);
        Assert.Equal("Unplugged", config.Config.Buttons[1].Default().SinglePress.DeviceB!.FriendlyName);
        Assert.Equal("Desk (Realtek)", seenByUi);
    }

    [Fact]
    public void MuteOsd_UsesTheNickname()
    {
        var config = NewConfig(c => c.Devices.Add(new DeviceSettingsConfig { Id = Speakers, Nickname = "Desk speakers" }));
        var runtime = StartRuntime(config);
        var volumes = new Recorder<VolumeChangeNotification>();
        runtime.VolumeChanged += volumes.Handler;

        Press(Connection(0), 0);                                  // button 0 mutes the default output
        _engine.DeviceStates[Speakers] = (0.5f, true);
        _engine.RaiseDeviceVolumeChanged(Speakers, 0.5f, muted: true, isOwnChange: true);

        Assert.True(WaitUntil(() => volumes.Count > 0));
        Assert.Equal("Desk speakers", volumes.Items[0].Label);
    }

    // ---- transport restart -----------------------------------------------------------------

    [Fact]
    public void Restart_ForwardsStatusFromNewTransportOnly()
    {
        var config = NewConfig();
        var runtime = StartRuntime(config);
        var statuses = new Recorder<TransportStatus>();
        runtime.TransportStatusChanged += statuses.Handler;
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

        oldConnection.SetStatus(TransportStatus.Open);
        Assert.Empty(statuses.Items);
        Assert.Equal(0, oldConnection.SentCount);

        newConnection.SetStatus(TransportStatus.Open);
        Assert.Equal(new[] { TransportStatus.Open }, statuses.Items);
        Assert.Equal(TransportStatus.Open, runtime.TransportStatus);
        Assert.True(newConnection.SentCount > 0, "state not pushed to the new transport");
    }

    /// <summary>Pins the user-visible case: restarting while the OLD transport is
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
        var statuses = new Recorder<TransportStatus>();
        runtime.TransportStatusChanged += statuses.Handler;
        var oldConnection = Connection(0);

        oldConnection.SetStatus(TransportStatus.Open);
        Assert.Equal(new[] { TransportStatus.Open }, statuses.Items);

        config.Config.Connection.Type = ConnectionType.WebSocket;
        config.RequestSave(connectionChanged: true); // debounced, raises ConnectionConfigChanged

        Assert.True(WaitUntil(() => ConnectionCount == 2 && Connection(1).ConnectCount == 1),
                    "transport was not restarted");
        var newConnection = Connection(1);

        Assert.Equal(new[] { TransportStatus.Open, TransportStatus.Closed }, statuses.Items);
        Assert.Equal(TransportStatus.Closed, runtime.TransportStatus);

        newConnection.SetStatus(TransportStatus.Open);
        Assert.Equal(new[]
        {
            TransportStatus.Open, TransportStatus.Closed, TransportStatus.Open,
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
        var config = NewConfig(c => c.Buttons[2].Default().SinglePress = ToggleWithA("", "Speakers"));
        var path = config.ConfigPath;

        StartRuntime(config);

        Assert.Equal(Speakers, ToggleA(config).Id);
        Assert.Contains(Speakers, File.ReadAllText(path)); // saved to disk, not just in memory
    }

    [Fact]
    public void Start_AudioNotReady_DoesNotHealDeviceIds()
    {
        var config = NewConfig(c => c.Buttons[2].Default().SinglePress = ToggleWithA("", "Speakers"));

        StartRuntime(config, audioReady: false);

        Assert.Equal("", ToggleA(config).Id);
    }

    private static ButtonActionConfig ToggleWithA(string id, string name) => new()
    {
        Action = ButtonActionType.ToggleOutputDevice,
        Device = new DeviceRefConfig { Id = id, FriendlyName = name },
    };

    private static DeviceRefConfig ToggleA(ConfigManager config) => config.Config.Buttons[2].Default().SinglePress.Device!;

    [Fact]
    public void DeviceAdded_HealsBlankDeviceId_ForADeviceThatWasAbsentAtStart()
    {
        const string NewDeviceId = "new-dev";
        // Not in _engine.Devices yet - Start()'s heal pass must leave this unhealed.
        var config = NewConfig(c => c.Buttons[2].Default().SinglePress = ToggleWithA("", "New Speakers"));

        StartRuntime(config);
        Assert.Equal("", ToggleA(config).Id);

        var newDevice = new AudioDevice(NewDeviceId, "New Speakers", AudioFlow.Render, false);
        _engine.Devices.Add(newDevice);
        _engine.RaiseDeviceChanged(DeviceChangeKind.Added, newDevice);

        Assert.True(WaitUntil(() => ToggleA(config).Id == NewDeviceId),
                    "device id was not healed after DevicesChanged(Added)");
    }

    [Fact]
    public void HealedOutputDevice_TogglePressEndsUpSettingTheHealedDevice()
    {
        var config = NewConfig(c =>
        {
            c.Buttons[2].Default().SinglePress = new ButtonActionConfig
            {
                Action = ButtonActionType.ToggleOutputDevice,
                Device = new DeviceRefConfig { Id = Speakers, FriendlyName = "Speakers" },
                DeviceB = new DeviceRefConfig { Id = "", FriendlyName = "Headphones" },
            };
        });

        StartRuntime(config);
        Assert.Equal(Headphones, config.Config.Buttons[2].Default().SinglePress.DeviceB!.Id); // healed at Start()

        Press(Connection(0), 2);

        Assert.True(WaitUntil(() => _engine.SetDefaultCalls.Count > 0), "no SetDefaultDevice call");
        Assert.Contains(Snapshot(_engine.SetDefaultCalls), c => c.EndpointId == Headphones);
    }

    // ---- Fix D: GetSliderLevel ---------------------------------------------------------------

    [Fact]
    public void GetSliderLevel_NullBeforeStart_ReflectsMessageAfterStart_NullAfterDispose()
    {
        var config = NewConfig();
        var runtime = new VolcoRuntime(config, _engine, audioReady: true, Factory, Quiet);

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
        var runtime = StartRuntime(options: Quiet);
        var connection = Connection(0);
        connection.SetStatus(TransportStatus.Open);

        var events = 0;
        runtime.VolumeChanged += (_, _) => Interlocked.Increment(ref events);
        runtime.SliderLevelChanged += (_, _) => Interlocked.Increment(ref events);
        runtime.TransportStatusChanged += (_, _) => Interlocked.Increment(ref events);
        runtime.DevicesChanged += (_, _) => Interlocked.Increment(ref events);

        runtime.Dispose();
        runtime.Dispose(); // idempotent

        Assert.Equal(1, connection.DisposeCount);
        Assert.False(_engine.Disposed);

        var sentAtDispose = connection.SentCount;
        _engine.DeviceStates[Speakers] = (0.5f, true);
        _engine.RaiseDeviceVolumeChanged(Speakers, 0.5f, muted: true, isOwnChange: true);
        _engine.RaiseDeviceChanged(DeviceChangeKind.DefaultChanged, _engine.Devices[1]);
        connection.SetStatus(TransportStatus.Closed);
        connection.RaiseMessage(new SliderMessage(0, 1000));

        // Covers the slider coalescing tick.
        Assert.True(StaysUnchanged(() => Volatile.Read(ref events) + connection.SentCount + _engine.DeviceVolumeCalls.Count));
        Assert.Equal(0, Volatile.Read(ref events));
        Assert.Equal(sentAtDispose, connection.SentCount);
        Assert.Empty(_engine.DeviceVolumeCalls);
    }

    // ---- modes (schema v3) ---------------------------------------------------------------

    /// <summary>Two modes: Default (button 0 mutes the output, button 2 holds for "next mode")
    /// and Gaming (button 0 mutes the input, button 2 jumps back to Default).</summary>
    private ConfigManager TwoModeConfig() => NewConfig(c =>
    {
        c.Modes.Add(new ModeConfig { Id = "gaming", Name = "Gaming" });
        c.Buttons[2].Default().SinglePress = new ButtonActionConfig { Action = ButtonActionType.NextMode };
        var gaming0 = c.Buttons[0].GetOrAddBinding("gaming");
        gaming0.SinglePress = new ButtonActionConfig { Action = ButtonActionType.MuteCurrentInput };
        c.Buttons[2].GetOrAddBinding("gaming").SinglePress =
            new ButtonActionConfig { Action = ButtonActionType.GoToMode, ModeId = AppConfig.DefaultModeId };
    });

    [Fact]
    public void ActiveMode_StartsAtTheFirstMode()
    {
        var runtime = StartRuntime(TwoModeConfig());
        Assert.Equal(AppConfig.DefaultModeId, runtime.ActiveModeId);
    }

    [Fact]
    public void ButtonDispatch_FollowsTheActiveMode()
    {
        var runtime = StartRuntime(TwoModeConfig());

        runtime.SetActiveMode("gaming");
        Press(Connection(0), 0);

        Assert.True(WaitUntil(() => _engine.ToggleMuteCalls.Count > 0), "press never executed");
        Assert.Equal(DeviceRef.DefaultInput(), Assert.Single(Snapshot(_engine.ToggleMuteCalls)));
    }

    [Fact]
    public void NextModeButton_SwitchesAndWraps_RaisingEventsAndAModeOsd()
    {
        var runtime = StartRuntime(TwoModeConfig());
        var changes = new Recorder<string>();
        var osd = new Recorder<VolumeChangeNotification>();
        runtime.ActiveModeChanged += changes.Handler;
        runtime.VolumeChanged += osd.Handler;

        Press(Connection(0), 2);   // Default: next mode -> Gaming
        Assert.True(WaitUntil(() => runtime.ActiveModeId == "gaming"), "next mode never ran");
        Press(Connection(0), 2);   // Gaming: go to Default
        Assert.True(WaitUntil(() => runtime.ActiveModeId == AppConfig.DefaultModeId), "go-to mode never ran");

        Assert.Equal(new[] { "gaming", AppConfig.DefaultModeId }, changes.Items);
        Assert.Equal(new[] { "Gaming", "Default" }, osd.Items.Where(n => n.Kind == OsdKind.Mode).Select(n => n.Label));
    }

    [Fact]
    public void NextMode_WrapsToTheFirst()
    {
        var runtime = StartRuntime(TwoModeConfig());
        runtime.NextMode();
        runtime.NextMode();
        Assert.Equal(AppConfig.DefaultModeId, runtime.ActiveModeId);
    }

    [Fact]
    public void SetActiveMode_UnknownId_IsIgnored()
    {
        var runtime = StartRuntime(TwoModeConfig());
        var changes = new Recorder<string>();
        runtime.ActiveModeChanged += changes.Handler;

        runtime.SetActiveMode("nope");

        Assert.Equal(AppConfig.DefaultModeId, runtime.ActiveModeId);
        Assert.Empty(changes.Items);
    }

    [Fact]
    public void DeletingTheActiveMode_FallsBackToTheFirst()
    {
        var config = TwoModeConfig();
        var runtime = StartRuntime(config);
        runtime.SetActiveMode("gaming");

        config.Config.Modes.RemoveAll(p => p.Id == "gaming");

        Assert.Equal(AppConfig.DefaultModeId, runtime.ActiveModeId);
    }

    [Fact]
    public void ModeChange_PushesTheNewModesFeedback()
    {
        var runtime = StartRuntime(TwoModeConfig());
        var connection = Connection(0);
        ConnectAndGreet(connection);
        _engine.DeviceStates[Microphone] = (0.5f, true);   // muted mic: lit only under Gaming
        var baseline = connection.SentCount;

        runtime.SetActiveMode("gaming");

        var newFrames = connection.SentFrames.Skip(baseline).ToList();
        Assert.True(ContainsFrame(newFrames, ProtocolEncoder.EncodeBegin()), "no Begin");
        Assert.True(ContainsFrame(newFrames, ProtocolEncoder.EncodeCommit()), "no Commit");
        Assert.True(ContainsFrame(newFrames, ProtocolEncoder.EncodeLed(0, 1)), "button 0 not lit for the muted mic");
        Assert.True(ContainsFrame(newFrames, ProtocolEncoder.EncodeLed(1, 0)), "button 1 has nothing in Gaming, so its light must go off");
        Assert.True(ContainsFrame(newFrames, ProtocolEncoder.EncodeLed(2, 2)), "mode indicator not on mode 2");
    }

    [Fact]
    public void Execute_RunsAnActionOutsideAButtonPress()
    {
        var runtime = StartRuntime(TwoModeConfig());

        runtime.Execute(new ButtonActionConfig { Action = ButtonActionType.GoToMode, ModeId = "gaming" });

        Assert.Equal("gaming", runtime.ActiveModeId);
    }

    [Fact]
    public void ButtonActivity_IsRaisedOnPressDown_EvenForUnconfiguredButtons()
    {
        var runtime = StartRuntime();
        var activity = new Recorder<int>();
        runtime.ButtonActivity += activity.Handler;

        Press(Connection(0), 2);
        Press(Connection(0), 9);   // beyond the configured buttons

        Assert.Equal(new[] { 2, 9 }, activity.Items);
    }

    [Fact]
    public void NotSetUpSlider_ChangesNoVolume_ButStillReportsItsLevel()
    {
        var runtime = StartRuntime(NewConfig(c => c.Sliders[0].Type = SliderMappingType.None));
        var levels = new Recorder<SliderLevelEventArgs>();
        var osd = new Recorder<VolumeChangeNotification>();
        runtime.SliderLevelChanged += levels.Handler;
        runtime.VolumeChanged += osd.Handler;

        Connection(0).RaiseMessage(new SliderMessage(0, 4095));

        Assert.True(WaitUntil(() => levels.Count > 0), "no level event");
        Assert.Empty(Snapshot(_engine.DeviceVolumeCalls));
        Assert.Empty(osd.Items);
    }

    [Fact]
    public void MoreThanSixteenSliders_AreAllRouted()
    {
        var runtime = StartRuntime(NewConfig(c =>
        {
            while (c.Sliders.Count < 20) c.Sliders.Add(new SliderConfig { Type = SliderMappingType.Device });
        }));
        var levels = new Recorder<SliderLevelEventArgs>();
        runtime.SliderLevelChanged += levels.Handler;

        Connection(0).RaiseMessage(new SliderMessage(19, 4095));

        Assert.True(WaitUntil(() => levels.Items.Any(e => e.SliderIndex == 19)), "slider 19 never processed");
        Assert.Equal(1f, runtime.GetSliderLevel(19)!.Value);
    }

    // The race through the runtime. The old connection's handler is detached on retirement, so
    // deliver through the service the way a message already past the runtime's sender check would.
    [Fact]
    public void AHelloAlreadyPastTheSenderCheck_WhenTheConnectionIsReplaced_IsDropped()
    {
        var runtime = StartRuntime();
        var old = Connection(0);
        ConnectAndGreet(old);
        runtime.Reconnect();
        runtime.ControllerConfig.OnMessage(old, LedHello);
        Assert.Null(runtime.ControllerConfig.LastHello);
    }
}
