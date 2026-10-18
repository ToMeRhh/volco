using Microsoft.Extensions.Time.Testing;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Media;
using Volco.Core.Programs;
using Volco.Core.Runtime;
using Volco.Core.Scripts;
using Volco.Core.Tests.Audio;
using Volco.Core.Tests.Fakes;
using Xunit;

namespace Volco.Core.Tests.Communication;

/// <summary>What media, program and script buttons show (custom-actions spec §3.3, §4, §5.4).</summary>
public class ControllerDisplayCustomActionTests
{
    private const string Game = @"C:\Games\doom.exe";
    private const string Script = @"C:\s\backup.bat";
    private readonly FakeAudioEngine _audio = new();
    private readonly FakeMediaSessions _media = new();
    private readonly FakeProgramLauncher _launcher = new();
    private readonly FakeProcessStarter _starter = new();
    private readonly FakeTimeProvider _time = new();
    private readonly ScriptRunner _scripts;
    private readonly ActionStatus _status;

    public ControllerDisplayCustomActionTests()
    {
        _scripts = new ScriptRunner(_starter, _time, n => n, p => p == Script);
        _status = new ActionStatus(_media, new ProgramOpener(_launcher, _time), _scripts);
    }

    private static readonly MediaApp Spotify = new("Spotify.exe", "Spotify", MediaPlayback.Playing);
    private static readonly MediaApp Chrome = new("chrome", "Google Chrome", MediaPlayback.Paused);

    private static ButtonActionConfig Media(ButtonActionType t, MediaAppConfig? app = null) => new() { Action = t, MediaApp = app };
    private static MediaAppConfig App(string id, string name) => new() { Id = id, Name = name };

    private static AppConfig Config(IndicatorKind kind, ButtonActionConfig action)
    {
        var c = AppConfig.CreateDefault();
        c.Buttons.Clear();
        var b = new ButtonConfig();
        b.Default().SinglePress = action;
        c.Buttons.Add(b);
        c.Controller = new ControllerInfoConfig { ProtocolVersion = 1, Sliders = 0, Buttons = new() { kind } };
        return c;
    }

    private ScreenContent ScreenOf(ButtonActionConfig a)
        => Assert.IsType<ScreenDisplay>(ControllerDisplay.Compute(Config(IndicatorKind.Screen, a), AppConfig.DefaultModeId, _audio, null, _status)[0]).Content;

    private byte LedOf(ButtonActionConfig a, IndicatorKind kind = IndicatorKind.Led)
        => Assert.IsType<LedDisplay>(ControllerDisplay.Compute(Config(kind, a), AppConfig.DefaultModeId, _audio, null, _status)[0]).Value;

    // ── Media ─────────────────────────────────────────────────────────────────

    [Fact]
    public void PlayPause_Current_ShowsTheCurrentAppsState()
    {
        _media.SetApps(new[] { Spotify, Chrome }, current: "Spotify.exe");
        var a = Media(ButtonActionType.MediaPlayPause);
        Assert.Equal(ScreenContent.Text("PLAYING", ScreenIcon.Equalizer, false, "Spotify"), ScreenOf(a));
        Assert.Equal(1, LedOf(a));

        _media.SetApps(new[] { Spotify, Chrome }, current: "chrome");
        Assert.Equal(ScreenContent.Text("PAUSED", ScreenIcon.EqualizerStill, false, "Google Chrome"), ScreenOf(a));
        Assert.Equal(0, LedOf(a));
    }

    [Fact]
    public void PlayPause_ChosenApp_FollowsThatApp_NotTheCurrentOne()
    {
        _media.SetApps(new[] { Spotify, Chrome }, current: "chrome");
        var a = Media(ButtonActionType.MediaPlayPause, App("Spotify.exe", "Spotify"));
        Assert.Equal(ScreenContent.Text("PLAYING", ScreenIcon.Equalizer, false, "Spotify"), ScreenOf(a));
        Assert.Equal(1, LedOf(a));
        Assert.Equal(1, LedOf(a, IndicatorKind.LedPair));
    }

    [Fact]
    public void ChosenAppNotRunning_ShowsItsNameDark()
    {
        _media.SetApps(new[] { Chrome });
        var a = Media(ButtonActionType.MediaPlayPause, App("Spotify.exe", "Spotify"));
        Assert.Equal(ScreenContent.Text("", ScreenIcon.Play, false, "Spotify"), ScreenOf(a));
        Assert.Equal(0, LedOf(a));
    }

    [Fact]
    public void NoCurrentSession_SaysMedia()
    {
        var a = Media(ButtonActionType.MediaPlayPause);
        Assert.Equal(ScreenContent.Text("", ScreenIcon.Play, false, "Media"), ScreenOf(a));
        Assert.Equal(0, LedOf(a));
    }

    [Fact]
    public void Stop_ShowsPlaybackLikePlayPause()
    {
        _media.SetApps(new[] { Spotify with { Playback = MediaPlayback.Stopped } });
        var a = Media(ButtonActionType.MediaStop);
        Assert.Equal(ScreenContent.Text("STOPPED", ScreenIcon.EqualizerStill, false, "Spotify"), ScreenOf(a));
        Assert.Equal(0, LedOf(a));
    }

    [Fact]
    public void NextAndPrevious_HaveNoState()
    {
        _media.SetApps(new[] { Spotify });
        Assert.Equal(ScreenContent.Text("", ScreenIcon.Next, false, "Spotify"), ScreenOf(Media(ButtonActionType.MediaNext)));
        Assert.Equal(ScreenContent.Text("", ScreenIcon.Previous, false, "Spotify"), ScreenOf(Media(ButtonActionType.MediaPrevious)));
        Assert.Equal(0, LedOf(Media(ButtonActionType.MediaNext)));
    }

    [Fact]
    public void WithoutStatus_MediaReadsAsIdle()
    {
        var c = Config(IndicatorKind.Screen, Media(ButtonActionType.MediaPlayPause, App("Spotify.exe", "Spotify")));
        var screen = Assert.IsType<ScreenDisplay>(ControllerDisplay.Compute(c, AppConfig.DefaultModeId, _audio)[0]).Content;
        Assert.Equal(ScreenContent.Text("", ScreenIcon.Play, false, "Spotify"), screen);
    }

    // ── Programs ──────────────────────────────────────────────────────────────

    [Fact]
    public void Program_LitWhileRunning()
    {
        var a = new ButtonActionConfig { Action = ButtonActionType.OpenProgram, Command = new CommandConfig { Path = Game } };
        Assert.Equal(ScreenContent.Text("", ScreenIcon.App, false, "doom"), ScreenOf(a));
        Assert.Equal(0, LedOf(a));

        _launcher.SetRunning(Game, true);
        Assert.Equal(ScreenContent.Text("RUNNING", ScreenIcon.App, false, "doom"), ScreenOf(a));
        Assert.Equal(1, LedOf(a));
    }

    [Fact]
    public void Program_UsesItsName_AndAsksForAFile()
    {
        var named = new ButtonActionConfig { Action = ButtonActionType.OpenProgram, Command = new CommandConfig { Path = "steam://rungameid/570", Name = "Dota 2" } };
        Assert.Equal(ScreenContent.Text("", ScreenIcon.App, false, "Dota 2"), ScreenOf(named));
        Assert.Equal(0, LedOf(named));
        var empty = new ButtonActionConfig { Action = ButtonActionType.OpenProgram };
        Assert.Equal(ScreenContent.Text("", ScreenIcon.App, false, "Choose a file"), ScreenOf(empty));
    }

    // ── Scripts ───────────────────────────────────────────────────────────────

    [Fact]
    public void Script_RunningThenIdle()
    {
        var a = new ButtonActionConfig { Action = ButtonActionType.RunScript, Command = new CommandConfig { Path = Script } };
        Assert.Equal(ScreenContent.Text("", ScreenIcon.Script, false, "backup"), ScreenOf(a));

        _scripts.Run(a, new Dictionary<string, string>());
        Assert.Equal(ScreenContent.Text("RUNNING", ScreenIcon.Script, false, "backup"), ScreenOf(a));
        Assert.Equal(1, LedOf(a));

        _starter.Runs[0].Run.End(0);
        Assert.Equal(0, LedOf(a));
    }

    [Fact]
    public void Script_FailureFlash_BlinksAndSaysFailed()
    {
        var a = new ButtonActionConfig { Action = ButtonActionType.RunScript, Command = new CommandConfig { Path = Script } };
        _scripts.Run(a, new Dictionary<string, string>());
        _starter.Runs[0].Run.End(1);

        Assert.Equal(ScreenContent.Text("FAILED", ScreenIcon.Script, false, "backup"), ScreenOf(a));
        var leds = new List<byte>();
        for (int i = 0; i < ScriptRunner.FlashSteps; i++)
        {
            leds.Add(LedOf(a));
            _time.Advance(ScriptRunner.FlashStepInterval);
        }
        Assert.Equal(Enumerable.Range(0, ScriptRunner.FlashSteps).Select(i => (byte)(i % 2 == 0 ? 1 : 0)), leds);
        Assert.Equal(ScreenContent.Text("", ScreenIcon.Script, false, "backup"), ScreenOf(a));
        Assert.Equal(0, LedOf(a));
    }
}
