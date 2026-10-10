using Microsoft.Extensions.Time.Testing;
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Media;
using Volco.Core.Runtime;
using Volco.Core.Scripts;
using Volco.Core.Tests.Audio;
using Volco.Core.Tests.Fakes;
using Xunit;

namespace Volco.Core.Tests.Communication;

/// <summary>What a button shows for a moment after a press: the pressed action, an error, a
/// Play/pause's expected state; and a failing script on any slot (custom-actions spec §11).</summary>
public class ControllerDisplayPressedTests
{
    private const string Script = @"C:\s\backup.bat";
    private static readonly AudioDevice Speakers = new("spk", "Speakers", AudioFlow.Render, true, DeviceFormFactor.Speakers);
    private readonly FakeAudioEngine _audio = new() { Devices = { Speakers }, DefaultRender = Speakers };
    private readonly FakeMediaSessions _media = new();
    private readonly FakeProcessStarter _starter = new();
    private readonly FakeTimeProvider _time = new();
    private readonly ScriptRunner _scripts;
    private readonly ActionStatus _status;

    public ControllerDisplayPressedTests()
    {
        _audio.DeviceStates["spk"] = (0.5f, false);
        _scripts = new ScriptRunner(_starter, _time, n => n, p => p == Script);
        _status = new ActionStatus(_media, null, _scripts);
        _media.SetApps(new[] { new MediaApp("Spotify.exe", "Spotify", MediaPlayback.Playing) });
    }

    private static readonly ScreenContent MuteLive = ScreenContent.Text("LIVE", ScreenIcon.Speaker, false, "Speakers");
    private static readonly ScreenContent Playing = ScreenContent.Text("PLAYING", ScreenIcon.Play, false, "Spotify");
    private static readonly ScreenContent Paused = ScreenContent.Text("PAUSED", ScreenIcon.Pause, false, "Spotify");

    /// <summary>Button 0: press mutes the output, double press plays/pauses Spotify, hold runs a script.</summary>
    private static AppConfig Config(IndicatorKind kind)
    {
        var c = AppConfig.CreateDefault();
        c.Buttons.Clear();
        var b = new ButtonConfig();
        var binding = b.Default();
        binding.SinglePress = new ButtonActionConfig { Action = ButtonActionType.MuteCurrentOutput };
        binding.DoublePress = new ButtonActionConfig
        {
            Action = ButtonActionType.MediaPlayPause, MediaApp = new MediaAppConfig { Id = "Spotify.exe", Name = "Spotify" },
        };
        binding.LongPress = new ButtonActionConfig { Action = ButtonActionType.RunScript, Command = new CommandConfig { Path = Script } };
        c.Buttons.Add(b);
        c.Controller = new ControllerInfoConfig { ProtocolVersion = 1, Buttons = new() { kind } };
        return c;
    }

    private static ButtonBindingConfig Binding(AppConfig c) => c.Buttons[0].Default();

    /// <summary>Feedback as the runtime makes it for a press of <paramref name="action"/>.</summary>
    private static PressFeedback Pressed(AppConfig c, ButtonActionConfig action, string? error = null, int step = 0,
                                         MediaPlayback? from = null, MediaPlayback? expect = null)
        => new(action, ControllerDisplay.ShowsWhenPressed(Binding(c), action), error, step, from, expect);

    private ButtonDisplay? Show(AppConfig c, PressFeedback? feedback)
        => ControllerDisplay.Compute(c, AppConfig.DefaultPresetId, _audio, null, _status,
                                     feedback is null ? null : new Dictionary<int, PressFeedback> { [0] = feedback })[0];

    private ScreenContent ScreenOf(AppConfig c, PressFeedback? feedback) => Assert.IsType<ScreenDisplay>(Show(c, feedback)).Content;
    private byte LedOf(AppConfig c, PressFeedback? feedback) => Assert.IsType<LedDisplay>(Show(c, feedback)).Value;

    // ── A press of another action ─────────────────────────────────────────────

    [Fact]
    public void Screen_ShowsThePressedAction_InsteadOfTheFirst()
    {
        var c = Config(IndicatorKind.Screen);
        Assert.Equal(MuteLive, ScreenOf(c, null));
        Assert.Equal(Playing, ScreenOf(c, Pressed(c, Binding(c).DoublePress)));
    }

    [Fact]
    public void Led_BlinksTwiceLong_ForAnotherAction()
    {
        var c = Config(IndicatorKind.Led);
        var leds = Enumerable.Range(0, ControllerDisplay.FeedbackSteps)
                             .Select(step => LedOf(c, Pressed(c, Binding(c).DoublePress, step: step)));
        Assert.Equal(new byte[] { 1, 1, 1, 0, 0, 0, 1, 1, 1, 0, 0, 0 }, leds);
    }

    [Fact]
    public void LedPair_BlinksItsASide()
    {
        var c = Config(IndicatorKind.LedPair);
        Assert.Equal(1, LedOf(c, Pressed(c, Binding(c).DoublePress, step: 0)));
        Assert.Equal(0, LedOf(c, Pressed(c, Binding(c).DoublePress, step: 3)));
    }

    [Fact]
    public void AfterTheLastStep_TheButtonShowsItsFirstActionAgain()
    {
        var c = Config(IndicatorKind.Screen);
        Assert.Equal(MuteLive, ScreenOf(c, Pressed(c, Binding(c).DoublePress, step: ControllerDisplay.FeedbackSteps)));
    }

    [Fact]
    public void AnActionOfAnotherBinding_IsIgnored()
    {
        var c = Config(IndicatorKind.Screen);
        var stranger = new ButtonActionConfig { Action = ButtonActionType.MediaPlayPause };
        Assert.Equal(MuteLive, ScreenOf(c, new PressFeedback(stranger, true, ControllerDisplay.NotRunning, 0)));
    }

    [Fact]
    public void ShowsWhenPressed_Rules()
    {
        var c = Config(IndicatorKind.Screen);
        var b = Binding(c);
        Assert.False(ControllerDisplay.ShowsWhenPressed(b, b.SinglePress));            // the first action: shown anyway
        Assert.True(ControllerDisplay.ShowsWhenPressed(b, b.DoublePress));
        Assert.True(ControllerDisplay.ShowsWhenPressed(b, b.LongPress));
        b.LongPress = new ButtonActionConfig { Action = ButtonActionType.NextPreset };
        Assert.False(ControllerDisplay.ShowsWhenPressed(b, b.LongPress));              // announced on its own
        b.LongPress = new ButtonActionConfig();
        Assert.False(ControllerDisplay.ShowsWhenPressed(b, b.LongPress));              // Do nothing
    }

    // ── Errors ────────────────────────────────────────────────────────────────

    [Fact]
    public void Error_ShowsTheErrorIcon_WhyAndWhat()
    {
        var c = Config(IndicatorKind.Screen);
        _media.SetApps(Array.Empty<MediaApp>());
        Assert.Equal(ScreenContent.Text("NOT RUNNING", ScreenIcon.Error, false, "Spotify"),
                     ScreenOf(c, Pressed(c, Binding(c).DoublePress, ControllerDisplay.NotRunning)));
        Assert.Equal(ScreenContent.Text("USB ONLY", ScreenIcon.Error, false, "backup"),
                     ScreenOf(c, Pressed(c, Binding(c).LongPress, ControllerDisplay.UsbOnly)));
    }

    [Fact]
    public void Error_OnTheFirstAction_ShowsToo()
    {
        var c = Config(IndicatorKind.Screen);
        Assert.Equal(ScreenContent.Text("NOT AVAILABLE", ScreenIcon.Error, false, ""),
                     ScreenOf(c, Pressed(c, Binding(c).SinglePress, ControllerDisplay.NotAvailable)));
    }

    [Fact]
    public void Error_FlashesTheLedFast_OnAnySlot()
    {
        var c = Config(IndicatorKind.Led);
        foreach (var action in Binding(c).All())
        {
            var leds = Enumerable.Range(0, ControllerDisplay.FeedbackSteps)
                                 .Select(step => LedOf(c, Pressed(c, action, ControllerDisplay.NotRunning, step)));
            Assert.Equal(new byte[] { 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0 }, leds);
        }
    }

    // ── Play/pause's expected state ───────────────────────────────────────────

    [Fact]
    public void PlayPause_ShowsTheExpectedState_UntilWindowsReportsIt()
    {
        var c = Config(IndicatorKind.Screen);
        var press = Pressed(c, Binding(c).DoublePress, from: MediaPlayback.Playing, expect: MediaPlayback.Paused);
        Assert.Equal(Paused, ScreenOf(c, press));                       // Spotify still says playing

        _media.SetApps(new[] { new MediaApp("Spotify.exe", "Spotify", MediaPlayback.Paused) });
        Assert.Equal(Paused, ScreenOf(c, press));                       // now it says so too

        _media.SetApps(new[] { new MediaApp("Spotify.exe", "Spotify", MediaPlayback.Playing) });
        Assert.Equal(Playing, ScreenOf(c, press with { ExpectFrom = null, Expect = null }));   // the real state after a while
    }

    [Fact]
    public void PlayPause_OnTheFirstAction_LightsTheExpectedState()
    {
        var c = Config(IndicatorKind.Led);
        var b = Binding(c);
        (b.SinglePress, b.DoublePress) = (b.DoublePress, b.SinglePress);   // Play/pause on press now
        Assert.Equal(1, LedOf(c, null));                                    // Spotify plays
        Assert.Equal(0, LedOf(c, Pressed(c, b.SinglePress, from: MediaPlayback.Playing, expect: MediaPlayback.Paused)));
    }

    // ── A failing script ──────────────────────────────────────────────────────

    [Fact]
    public void AFailingScript_ShowsOnAnySlot_OnScreenAndLight()
    {
        var c = Config(IndicatorKind.Screen);
        _scripts.Run(Binding(c).LongPress, new Dictionary<string, string>());
        _starter.Runs[0].Run.End(1);

        var failed = ScreenContent.Text("FAILED", ScreenIcon.Script, false, "backup");
        Assert.Equal(failed, ScreenOf(c, null));
        Assert.Equal(failed, ScreenOf(c, Pressed(c, Binding(c).DoublePress)));   // over a pressed action too
        Assert.Equal(1, LedOf(Rebind(Config(IndicatorKind.Led), c), null));

        for (int i = 0; i < ScriptRunner.FlashSteps; i++) _time.Advance(ScriptRunner.FlashStepInterval);
        Assert.Equal(MuteLive, ScreenOf(c, null));
    }

    /// <summary><paramref name="target"/> with <paramref name="source"/>'s button (the same action
    /// objects, so the runner's state applies).</summary>
    private static AppConfig Rebind(AppConfig target, AppConfig source)
    {
        target.Buttons[0] = source.Buttons[0];
        return target;
    }
}
