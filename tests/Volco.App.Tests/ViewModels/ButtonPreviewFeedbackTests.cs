using Microsoft.Extensions.Time.Testing;
using Volco.App.ViewModels;
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Input;
using Volco.Core.Input.Actions;
using Volco.Core.Runtime;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.App.Tests.ViewModels;

/// <summary>The light and screen previews show a press's feedback as the controller does
/// (<c>ControllerDisplay.For</c> on the binding):
/// an error flash, and the pressed action when it is not the main one.</summary>
public sealed class ButtonPreviewFeedbackTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly TestRig _rig;
    private readonly List<int> _changed = new();

    public ButtonPreviewFeedbackTests()
    {
        var speakers = new AudioDevice("dev-1", "Speakers", AudioFlow.Render, true);
        _rig = new TestRig(new FakeAudioEngine { Devices = { speakers }, DefaultRender = speakers },
                           TestRig.NoGrace with { Time = _time });
        Config.Controller = new ControllerInfoConfig
        {
            ProtocolVersion = 1, Sliders = 0,
            Buttons = new List<IndicatorKind> { IndicatorKind.LedPair, IndicatorKind.Screen },
        };
        _rig.Runtime.FeedbackChanged += (_, button) => { lock (_changed) _changed.Add(button); };
    }

    public void Dispose() => _rig.Dispose();

    private AppConfig Config => _rig.Config.Config;

    private ButtonsViewModel Buttons()
        => new(_rig.Config, _rig.Engine, _rig.Runtime, _rig.Runtime.Execute, new UndoToastViewModel(),
               ActionCapabilities.Of(_rig.Runtime));

    [Fact]
    public void TryAnActionThatCannotRun_PreviewFlashesTheError()
    {
        // No media service in the rig: Play/pause is not available.
        Config.Buttons[1].Default().SinglePress.Action = ButtonActionType.MediaPlayPause;
        Config.Buttons[0].Default().SinglePress.Action = ButtonActionType.MediaPlayPause;
        var buttons = Buttons();

        _rig.Runtime.Execute(Config.Buttons[1].Default().SinglePress, new ActionOrigin(1, PressType.Single, "Default"));
        _rig.Runtime.Execute(Config.Buttons[0].Default().SinglePress, new ActionOrigin(0, PressType.Single, "Default"));
        buttons.RefreshFeedback(0);
        buttons.RefreshFeedback(1);

        Assert.Equal(ControllerDisplay.NotAvailable, buttons.Items[1].Screen.TopText);
        Assert.Equal(ScreenIcon.Error, buttons.Items[1].Screen.Icon);
        Assert.Equal(1, buttons.Items[0].LightValue);   // step 0: on
        lock (_changed) Assert.Contains(0, _changed);

        _time.Advance(ControllerDisplay.FeedbackStep);
        buttons.RefreshFeedback(0);
        Assert.Equal(0, buttons.Items[0].LightValue);   // step 1: off

        _time.Advance(ControllerDisplay.FeedbackStep * ControllerDisplay.FeedbackSteps);
        buttons.RefreshFeedback(-1);
        Assert.NotEqual(ControllerDisplay.NotAvailable, buttons.Items[1].Screen.TopText);
    }

    [Fact]
    public void TryTheDoublePress_ScreenShowsItForAMoment()
    {
        var binding = Config.Buttons[1].Default();
        binding.SinglePress.Action = ButtonActionType.MuteCurrentOutput;
        binding.DoublePress.Action = ButtonActionType.MuteCurrentInput;
        var buttons = Buttons();
        var before = buttons.Items[1].Screen;

        _rig.Runtime.Execute(binding.DoublePress, new ActionOrigin(1, PressType.Double, "Default"));
        buttons.RefreshFeedback(1);

        Assert.NotEqual(before, buttons.Items[1].Screen);
        Assert.Equal(ControllerDisplay.Screen(binding.DoublePress, Config, AppConfig.DefaultModeId, _rig.Engine, _rig.Runtime.ActionStatus),
                     buttons.Items[1].Screen);
    }

    [Fact]
    public void ExecuteWithoutAButton_NoFeedback()
    {
        var action = new ButtonActionConfig { Action = ButtonActionType.MediaPlayPause };

        _rig.Runtime.Execute(action);

        Assert.Null(_rig.Runtime.FeedbackFor(0));
        Assert.Null(_rig.Runtime.FeedbackFor(1));
    }
}
