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

/// <summary>A screen shows the action a press just ran; a failing script shows on any slot
/// (custom-actions spec §11).</summary>
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

    /// <summary>Button 0: press mutes the output, double press plays/pauses, hold runs a script.</summary>
    private static AppConfig Config(IndicatorKind kind)
    {
        var c = AppConfig.CreateDefault();
        c.Buttons.Clear();
        var b = new ButtonConfig();
        var binding = b.Default();
        binding.SinglePress = new ButtonActionConfig { Action = ButtonActionType.MuteCurrentOutput };
        binding.DoublePress = new ButtonActionConfig { Action = ButtonActionType.MediaPlayPause };
        binding.LongPress = new ButtonActionConfig { Action = ButtonActionType.RunScript, Command = new CommandConfig { Path = Script } };
        c.Buttons.Add(b);
        c.Controller = new ControllerInfoConfig { ProtocolVersion = 1, Buttons = new() { kind } };
        return c;
    }

    private static ButtonBindingConfig Binding(AppConfig c) => c.Buttons[0].Default();

    private ButtonDisplay? Show(AppConfig c, ButtonActionConfig? pressed)
        => ControllerDisplay.Compute(c, AppConfig.DefaultPresetId, _audio, null, _status,
                                     pressed is null ? null : new Dictionary<int, ButtonActionConfig> { [0] = pressed })[0];

    [Fact]
    public void Screen_ShowsThePressedAction_InsteadOfTheFirst()
    {
        var c = Config(IndicatorKind.Screen);
        Assert.Equal(MuteLive, Assert.IsType<ScreenDisplay>(Show(c, null)).Content);
        Assert.Equal(Playing, Assert.IsType<ScreenDisplay>(Show(c, Binding(c).DoublePress)).Content);
    }

    [Fact]
    public void Led_KeepsFollowingTheFirstAction()
    {
        var c = Config(IndicatorKind.Led);
        // The output is not muted: the mute light is off, although Spotify plays.
        Assert.Equal(new LedDisplay(0), Show(c, Binding(c).DoublePress));
    }

    [Fact]
    public void AnActionOfAnotherBinding_IsIgnored()
    {
        var c = Config(IndicatorKind.Screen);
        var stranger = new ButtonActionConfig { Action = ButtonActionType.MediaPlayPause };
        Assert.Equal(MuteLive, Assert.IsType<ScreenDisplay>(Show(c, stranger)).Content);
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

    [Fact]
    public void AFailingScript_ShowsOnAnySlot_OnScreenAndLight()
    {
        var c = Config(IndicatorKind.Screen);
        _scripts.Run(Binding(c).LongPress, new Dictionary<string, string>());
        _starter.Runs[0].Run.End(1);

        var failed = ScreenContent.Text("FAILED", ScreenIcon.Script, false, "backup");
        Assert.Equal(failed, Assert.IsType<ScreenDisplay>(Show(c, null)).Content);
        // Over a pressed action too: the failure is the news.
        Assert.Equal(failed, Assert.IsType<ScreenDisplay>(Show(c, Binding(c).DoublePress)).Content);
        Assert.Equal(new LedDisplay(1), Show(Rebind(Config(IndicatorKind.Led), c), null));

        for (int i = 0; i < ScriptRunner.FlashSteps; i++) _time.Advance(ScriptRunner.FlashStepInterval);
        Assert.Equal(MuteLive, Assert.IsType<ScreenDisplay>(Show(c, null)).Content);
    }

    /// <summary><paramref name="target"/> with <paramref name="source"/>'s button (the same action
    /// objects, so the runner's state applies).</summary>
    private static AppConfig Rebind(AppConfig target, AppConfig source)
    {
        target.Buttons[0] = source.Buttons[0];
        return target;
    }
}
