using System.Diagnostics;
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Input;
using Volco.Core.Input.Actions;
using Volco.Core.Media;
using Volco.Core.Runtime;
using Volco.Core.Tests.Audio;
using Volco.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Volco.Core.Tests.Runtime;

/// <summary>Media, program and script presses through <see cref="VolcoRuntime"/>, and the
/// USB-only rule (custom-actions spec §7).</summary>
public sealed class CustomActionRuntimeTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(2);

    private static readonly VolcoRuntimeOptions Quiet = new()
    {
        Link = new ControllerLinkOptions(TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.FromHours(1)),
        ConnectGrace = TimeSpan.Zero,
    };

    private readonly FakeAudioEngine _engine = new();
    private readonly FakeMediaSessions _media = new();
    private readonly FakeProgramLauncher _launcher = new();
    private readonly FakeProcessStarter _starter = new();
    private readonly List<FakeControllerConnection> _connections = new();
    private readonly List<VolcoRuntime> _runtimes = new();
    private readonly string _dir;
    private readonly string _script;

    public CustomActionRuntimeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"volco-custom-{Guid.NewGuid()}");
        Directory.CreateDirectory(_dir);
        _script = Path.Combine(_dir, "backup.bat");
        File.WriteAllText(_script, "@echo off\r\n");
        var speakers = new AudioDevice("spk", "Speakers", AudioFlow.Render, true);
        _engine.Devices = new List<AudioDevice> { speakers };
        _engine.DefaultRender = speakers;
        // Spotify is open and paused (the current app), unless a test says otherwise.
        _media.SetApps(new[] { new MediaApp("Spotify.exe", "Spotify", MediaPlayback.Paused) });
    }

    public void Dispose()
    {
        foreach (var runtime in _runtimes) runtime.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private FakeControllerConnection Connection(int index) { lock (_connections) return _connections[index]; }

    private VolcoRuntime Start(ConnectionType transport, Action<AppConfig> configure, IndicatorKind kind = IndicatorKind.Screen,
                               VolcoRuntimeOptions? options = null)
    {
        var cm = new ConfigManager(Path.Combine(_dir, $"config-{Guid.NewGuid()}.json"));
        cm.Load();
        cm.Config.Connection.Type = transport;
        foreach (var b in cm.Config.Buttons) b.DoublePressWindowMs = 30;
        configure(cm.Config);
        var runtime = new VolcoRuntime(cm, _engine, true, config =>
        {
            var connection = new FakeControllerConnection(config);
            lock (_connections) _connections.Add(connection);
            return connection;
        }, options ?? Quiet, new CustomActionServices(_media, _launcher, _starter));
        _runtimes.Add(runtime);
        runtime.Start();
        var c = Connection(0);
        c.SetStatus(TransportStatus.Open);
        c.RaiseMessage(new HelloMessage(ProtocolVersion.Current, 0, new[] { kind, kind, kind }));
        return runtime;
    }

    private static void Press(FakeControllerConnection connection, int button)
    {
        connection.RaiseMessage(new ButtonMessage(button, ButtonState.Pressed));
        connection.RaiseMessage(new ButtonMessage(button, ButtonState.Released));
    }

    private static bool WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > WaitTimeout) return condition();
            Thread.Sleep(5);
        }
        return true;
    }

    private static bool StaysFalse(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromMilliseconds(200))
        {
            if (condition()) return false;
            Thread.Sleep(5);
        }
        return !condition();
    }

    private void Script(AppConfig c) => c.Buttons[0].Default().SinglePress = new ButtonActionConfig
    {
        Action = ButtonActionType.RunScript, Command = new CommandConfig { Path = _script },
    };

    [Fact]
    public void ScriptPressOverSerial_Runs_WithTheEnvironment()
    {
        Start(ConnectionType.Serial, Script);
        Press(Connection(0), 0);

        Assert.True(WaitUntil(() => _starter.Runs.Count == 1), "the script did not run");
        var env = _starter.Runs[0].Spec.Environment;
        Assert.Equal("0", env["VOLCO_BUTTON"]);
        Assert.Equal("single", env["VOLCO_GESTURE"]);
        Assert.Equal("Default", env["VOLCO_PRESET"]);
    }

    [Theory]
    [InlineData(ConnectionType.Udp)]
    [InlineData(ConnectionType.WebSocket)]
    public void ScriptPressOverTheNetwork_IsRefused(ConnectionType transport)
    {
        Start(transport, Script);
        Press(Connection(0), 0);
        Assert.True(StaysFalse(() => _starter.Runs.Count > 0), "a network press ran a script");
    }

    [Theory]
    [InlineData(ConnectionType.Udp)]
    [InlineData(ConnectionType.WebSocket)]
    public void ProgramPressOverTheNetwork_IsRefused(ConnectionType transport)
    {
        Start(transport, c => c.Buttons[0].Default().SinglePress = new ButtonActionConfig
        {
            Action = ButtonActionType.OpenProgram, Command = new CommandConfig { Path = @"C:\Games\doom.exe" },
        });
        Press(Connection(0), 0);
        Assert.True(StaysFalse(() => _launcher.Started.Count > 0), "a network press started a program");
    }

    [Fact]
    public void ProgramPressOverSerial_StartsIt()
    {
        Start(ConnectionType.Serial, c => c.Buttons[0].Default().SinglePress = new ButtonActionConfig
        {
            Action = ButtonActionType.OpenProgram, Command = new CommandConfig { Path = @"C:\Games\doom.exe" },
        });
        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => _launcher.Started.Count == 1), "the program was not started");
    }

    [Fact]
    public void LivePresetsPrograms_AreWatched()
    {
        Start(ConnectionType.Serial, c => c.Buttons[1].Default().LongPress = new ButtonActionConfig
        {
            Action = ButtonActionType.OpenProgram, Command = new CommandConfig { Path = @"C:\Games\doom.exe" },
        });
        Assert.Equal(new[] { @"C:\Games\doom.exe" }, _launcher.Watched);
    }

    [Fact]
    public void MediaPressOverUdp_Runs()
    {
        Start(ConnectionType.Udp, c => c.Buttons[0].Default().SinglePress = new ButtonActionConfig
        {
            Action = ButtonActionType.MediaPlayPause, MediaApp = new MediaAppConfig { Id = "Spotify.exe", Name = "Spotify" },
        });
        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => _media.Sent.Count == 1), "no media command");
        Assert.Equal(("Spotify.exe", MediaCommand.PlayPause), _media.Sent[0]);
    }

    [Fact]
    public void MediaCurrent_SendsToTheCurrentApp()
    {
        Start(ConnectionType.Serial, c => c.Buttons[0].Default().SinglePress = new ButtonActionConfig { Action = ButtonActionType.MediaNext });
        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => _media.Sent.Count == 1), "no media command");
        Assert.Equal(((string?)null, MediaCommand.Next), _media.Sent[0]);
    }

    [Fact]
    public void MediaChange_PushesTheScreen()
    {
        Start(ConnectionType.Serial, c => c.Buttons[0].Default().SinglePress = new ButtonActionConfig
        {
            Action = ButtonActionType.MediaPlayPause, MediaApp = new MediaAppConfig { Id = "Spotify.exe", Name = "Spotify" },
        });
        var playing = ProtocolEncoder.EncodeScreen(0, ScreenTransition.None,
            ScreenContent.Text("PLAYING", ScreenIcon.Equalizer, false, "Spotify"));

        _media.SetApps(new[] { new MediaApp("Spotify.exe", "Spotify", MediaPlayback.Playing) });

        Assert.True(WaitUntil(() => Connection(0).SentFrames.Any(f => f.AsSpan().SequenceEqual(playing))),
                    "the playing screen was not sent");
    }

    [Fact]
    public void TestButton_RunsAScriptWhateverTheTransport()
    {
        var runtime = Start(ConnectionType.Udp, Script);
        var action = runtime.Config.Config.Buttons[0].Default().SinglePress;
        runtime.Execute(action, new ActionOrigin(0, PressType.Long, "Default"));
        Assert.True(WaitUntil(() => _starter.Runs.Count == 1), "Test did not run the script");
        Assert.Equal("long", _starter.Runs[0].Spec.Environment["VOLCO_GESTURE"]);
    }

    [Fact]
    public void ScriptFailure_PushesTheFailedScreen()
    {
        Start(ConnectionType.Serial, Script);
        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => _starter.Runs.Count == 1), "the script did not run");
        _starter.Runs[0].Run.End(1);

        var failed = ProtocolEncoder.EncodeScreen(0, ScreenTransition.None, ScreenContent.Text("FAILED", ScreenIcon.Script, false, "backup"));
        Assert.True(WaitUntil(() => Connection(0).SentFrames.Any(f => f.AsSpan().SequenceEqual(failed))),
                    "the failed screen was not sent");
    }

    [Fact]
    public void WithoutServices_CustomActionsDoNothing()
    {
        var cm = new ConfigManager(Path.Combine(_dir, "bare.json"));
        cm.Load();
        Script(cm.Config);
        var runtime = new VolcoRuntime(cm, _engine, true, config => new FakeControllerConnection(config), Quiet);
        _runtimes.Add(runtime);
        runtime.Start();
        Assert.Null(runtime.Media);
        Assert.False(runtime.ScriptsAvailable);
        Assert.False(runtime.ProgramsAvailable);
        runtime.Execute(cm.Config.Buttons[0].Default().SinglePress);   // nothing to run it with; no throw
    }

    // ── Press feedback (custom-actions spec §11) ─────────────────────────────

    private int Count(byte[] frame) => Connection(0).SentFrames.Count(f => f.AsSpan().SequenceEqual(frame));

    private static byte[] ScreenFrame(string top, ScreenIcon icon, string main)
        => ProtocolEncoder.EncodeScreen(0, ScreenTransition.None, ScreenContent.Text(top, icon, false, main));

    [Fact]
    public void DoublePress_ShowsTheExpectedState_ThenTheRealOne_ThenTheFirstActionAgain()
    {
        var time = new FakeTimeProvider();
        var runtime = Start(ConnectionType.Serial, c =>
        {
            c.Buttons[0].Default().SinglePress = new ButtonActionConfig { Action = ButtonActionType.MuteCurrentOutput };
            c.Buttons[0].Default().DoublePress = new ButtonActionConfig
            {
                Action = ButtonActionType.MediaPlayPause, MediaApp = new MediaAppConfig { Id = "Spotify.exe", Name = "Spotify" },
            };
        }, options: Quiet with { Time = time });
        var binding = runtime.Config.Config.Buttons[0].Default();
        var first = ProtocolEncoder.EncodeScreen(0, ScreenTransition.None,
            ControllerDisplay.Screen(binding.SinglePress, runtime.Config.Config, runtime.ActivePresetId, _engine));
        var expected = ScreenFrame("PLAYING", ScreenIcon.Equalizer, "Spotify");
        var real = ScreenFrame("PAUSED", ScreenIcon.EqualizerStill, "Spotify");
        Assert.True(WaitUntil(() => Count(first) == 1), "the first action's screen was not sent");

        Press(Connection(0), 0);
        Press(Connection(0), 0);

        Assert.True(WaitUntil(() => _media.Sent.Count == 1), "the double press did not play/pause");
        // Spotify (the fake) never reports the change: the screen says PLAYING at once anyway.
        Assert.True(WaitUntil(() => Count(expected) == 1), "the expected state was not shown");
        Assert.Equal(0, Count(real));

        time.Advance(ControllerDisplay.FeedbackStep * ControllerDisplay.ExpectSteps);
        Assert.True(WaitUntil(() => Count(real) == 1), "the real state did not win after a while");
        Assert.Equal(1, Count(first));

        time.Advance(ControllerDisplay.FeedbackStep * (ControllerDisplay.FeedbackSteps - ControllerDisplay.ExpectSteps));
        Assert.True(WaitUntil(() => Count(first) == 2), "the screen did not go back to the first action");
    }

    [Fact]
    public void DoublePress_BlinksAnLedTwice()
    {
        var time = new FakeTimeProvider();
        Start(ConnectionType.Serial, c =>
        {
            c.Buttons[0].Default().SinglePress = new ButtonActionConfig { Action = ButtonActionType.MuteCurrentOutput };
            c.Buttons[0].Default().DoublePress = new ButtonActionConfig { Action = ButtonActionType.MediaNext };
        }, IndicatorKind.Led, Quiet with { Time = time });
        var on = ProtocolEncoder.EncodeLed(0, 1);
        var off = ProtocolEncoder.EncodeLed(0, 0);
        Assert.True(WaitUntil(() => Count(off) == 1), "the mute light was not sent");

        Press(Connection(0), 0);
        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => Count(on) == 1), "the first blink did not start");

        for (int i = 0; i < ControllerDisplay.FeedbackSteps; i++) time.Advance(ControllerDisplay.FeedbackStep);
        // On, off, on, off: the last off is the mute light again (not muted).
        Assert.True(WaitUntil(() => Count(on) == 2 && Count(off) == 3), $"blinks: on {Count(on)}, off {Count(off)}");
    }

    [Fact]
    public void MediaPress_AppNotRunning_ShowsTheError_AndSendsNothing()
    {
        Start(ConnectionType.Serial, c => c.Buttons[0].Default().SinglePress = new ButtonActionConfig
        {
            Action = ButtonActionType.MediaPlayPause, MediaApp = new MediaAppConfig { Id = "foobar.exe", Name = "foobar" },
        });
        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => Count(ScreenFrame("NOT RUNNING", ScreenIcon.Error, "foobar")) == 1), "no error screen");
        Assert.Empty(_media.Sent);
    }

    [Fact]
    public void MediaPress_NothingPlaying_ShowsNoMedia()
    {
        _media.SetApps(Array.Empty<MediaApp>(), current: null);
        Start(ConnectionType.Serial, c => c.Buttons[0].Default().SinglePress = new ButtonActionConfig { Action = ButtonActionType.MediaNext });
        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => Count(ScreenFrame("NO MEDIA", ScreenIcon.Error, "Media")) == 1), "no error screen");
    }

    [Fact]
    public void MediaPress_TheAppRefuses_ShowsNotAvailable()
    {
        _media.Accepts = false;
        Start(ConnectionType.Serial, c => c.Buttons[0].Default().SinglePress = new ButtonActionConfig { Action = ButtonActionType.MediaNext });
        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => Count(ScreenFrame("NOT AVAILABLE", ScreenIcon.Error, "Spotify")) == 1), "no error screen");
    }

    [Fact]
    public void ScriptPressOverUdp_ShowsUsbOnly()
    {
        Start(ConnectionType.Udp, Script);
        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => Count(ScreenFrame("USB ONLY", ScreenIcon.Error, "backup")) == 1), "no error screen");
    }

    [Fact]
    public void ProgramThatCannotStart_ShowsCantOpen()
    {
        _launcher.StartSucceeds = false;
        Start(ConnectionType.Serial, c => c.Buttons[0].Default().SinglePress = new ButtonActionConfig
        {
            Action = ButtonActionType.OpenProgram, Command = new CommandConfig { Path = @"C:\Games\doom.exe" },
        });
        Press(Connection(0), 0);
        Assert.True(WaitUntil(() => Count(ScreenFrame("CAN'T OPEN", ScreenIcon.Error, "doom")) == 1), "no error screen");
    }
}
