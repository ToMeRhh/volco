using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.Core.Tests.Communication;

public class ButtonFeedbackTests
{
    private static readonly AudioDevice Speakers   = new("spk", "Speakers",   AudioFlow.Render, true);
    private static readonly AudioDevice Headphones = new("hp",  "Headphones", AudioFlow.Render, false);
    private static readonly AudioDevice Monitor    = new("mon", "Monitor",    AudioFlow.Render, false);
    private static readonly AudioDevice Mic        = new("mic", "Microphone", AudioFlow.Capture, true);

    private readonly FakeAudioEngine _audio = new() { DefaultRender = Speakers, DefaultCapture = Mic };

    private static DeviceRefConfig Ref(AudioDevice d) => new() { Id = d.Id, FriendlyName = d.FriendlyName };
    private static ButtonActionConfig Act(ButtonActionType t) => new() { Action = t };

    private static AppConfig Config(params ButtonActionConfig[] singlePresses)
    {
        var c = AppConfig.CreateDefault();
        c.Buttons.Clear();
        foreach (var a in singlePresses)
        {
            var b = new ButtonConfig();
            b.Default().SinglePress = a;
            c.Buttons.Add(b);
        }
        return c;
    }

    private int Value(AppConfig c, int button = 0, string preset = AppConfig.DefaultPresetId)
        => ButtonFeedback.Compute(c, preset, _audio)[button];

    [Fact]
    public void MuteCurrentOutput_OneWhileMuted()
    {
        var c = Config(Act(ButtonActionType.MuteCurrentOutput));
        _audio.DeviceStates["spk"] = (0.5f, false);
        Assert.Equal(0, Value(c));
        _audio.DeviceStates["spk"] = (0.5f, true);
        Assert.Equal(1, Value(c));
    }

    [Fact]
    public void MuteCurrentInput_FollowsTheDefaultInput()
    {
        var c = Config(Act(ButtonActionType.MuteCurrentInput));
        _audio.DeviceStates["mic"] = (1f, true);
        Assert.Equal(1, Value(c));
    }

    [Fact]
    public void MuteSpecificDevice_FollowsThatDevice_AndIsOffWhenUnset()
    {
        var c = Config(new ButtonActionConfig { Action = ButtonActionType.MuteSpecificDevice, Device = Ref(Headphones) },
                       Act(ButtonActionType.MuteSpecificDevice));
        _audio.DeviceStates["hp"] = (1f, true);
        Assert.Equal(1, Value(c, 0));
        Assert.Equal(0, Value(c, 1));
    }

    [Fact]
    public void SetOutputDevice_OneWhileThatDeviceIsTheOutput()
    {
        var c = Config(new ButtonActionConfig { Action = ButtonActionType.SetOutputDevice, Device = Ref(Speakers) },
                       new ButtonActionConfig { Action = ButtonActionType.SetOutputDevice, Device = Ref(Headphones) });
        Assert.Equal(1, Value(c, 0));
        Assert.Equal(0, Value(c, 1));
    }

    [Fact]
    public void ToggleOutputDevice_OneForA_TwoForB_ZeroForNeither()
    {
        var c = Config(new ButtonActionConfig
        {
            Action = ButtonActionType.ToggleOutputDevice, Device = Ref(Speakers), DeviceB = Ref(Headphones),
        });
        Assert.Equal(1, Value(c));
        _audio.DefaultRender = Headphones;
        Assert.Equal(2, Value(c));
        _audio.DefaultRender = Monitor;
        Assert.Equal(0, Value(c));
    }

    [Fact]
    public void ToggleOutputDevice_MatchesAFriendlyNameOnlyDevice()
    {
        var c = Config(new ButtonActionConfig
        {
            Action = ButtonActionType.ToggleOutputDevice,
            Device = new DeviceRefConfig { Id = "", FriendlyName = "Speakers" },
            DeviceB = Ref(Headphones),
        });
        Assert.Equal(1, Value(c));
    }

    [Fact]
    public void PresetActions_ReportOnePlusTheLivePresetsPosition()
    {
        var c = Config(Act(ButtonActionType.NextPreset));
        c.Presets.Add(new PresetConfig { Id = "gaming", Name = "Gaming" });
        c.Buttons[0].GetOrAddBinding("gaming").SinglePress = new ButtonActionConfig { Action = ButtonActionType.GoToPreset, PresetId = "default" };

        Assert.Equal(1, Value(c, 0, AppConfig.DefaultPresetId));
        Assert.Equal(2, Value(c, 0, "gaming"));
    }

    [Fact]
    public void FirstStatefulAction_WinsInSingleDoubleLongOrder()
    {
        var c = Config(Act(ButtonActionType.None));
        c.Buttons[0].Default().DoublePress = Act(ButtonActionType.MuteCurrentInput);
        c.Buttons[0].Default().LongPress = Act(ButtonActionType.MuteCurrentOutput);
        _audio.DeviceStates["mic"] = (1f, true);
        _audio.DeviceStates["spk"] = (1f, false);

        Assert.Equal(1, Value(c));   // the double-press mic mute, not the long-press output mute
    }

    [Fact]
    public void ButtonWithNoStatefulAction_IsZero()
    {
        var c = Config(Act(ButtonActionType.None));
        Assert.Equal(0, Value(c));
    }

    [Fact]
    public void ValuesComeFromTheLivePreset()
    {
        var c = Config(Act(ButtonActionType.MuteCurrentOutput));
        c.Presets.Add(new PresetConfig { Id = "gaming", Name = "Gaming" });
        _audio.DeviceStates["spk"] = (1f, true);

        Assert.Equal(1, Value(c, 0, AppConfig.DefaultPresetId));
        Assert.Equal(0, Value(c, 0, "gaming"));   // nothing bound in Gaming: the light goes off
    }

    [Fact]
    public void BuildFrames_OneFramePerButton_ByPosition()
    {
        var c = Config(Act(ButtonActionType.None), Act(ButtonActionType.MuteCurrentOutput), Act(ButtonActionType.None));
        _audio.DeviceStates["spk"] = (1f, true);

        var frames = ButtonFeedback.BuildFrames(c, AppConfig.DefaultPresetId, _audio);

        Assert.Equal(3, frames.Count);
        Assert.Equal(ProtocolEncoder.EncodeLed(0, 0), frames[0]);
        Assert.Equal(ProtocolEncoder.EncodeLed(1, 1), frames[1]);
        Assert.Equal(ProtocolEncoder.EncodeLed(2, 0), frames[2]);
    }
}
