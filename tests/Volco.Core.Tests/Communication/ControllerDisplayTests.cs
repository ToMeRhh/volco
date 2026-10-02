using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.Core.Tests.Communication;

public class ControllerDisplayTests
{
    private static readonly AudioDevice Speakers   = new("spk", "Speakers (Realtek)", AudioFlow.Render, true, DeviceFormFactor.Speakers);
    private static readonly AudioDevice Headphones = new("hp",  "Headphones",         AudioFlow.Render, false, DeviceFormFactor.Headphones);
    private static readonly AudioDevice Monitor    = new("mon", "Monitor",            AudioFlow.Render, false, DeviceFormFactor.Display);
    private static readonly AudioDevice Mic        = new("mic", "Microphone (Yeti)",  AudioFlow.Capture, true, DeviceFormFactor.Microphone);

    private readonly FakeAudioEngine _audio = new()
    {
        Devices = { Speakers, Headphones, Monitor, Mic }, DefaultRender = Speakers, DefaultCapture = Mic,
    };

    private static DeviceRefConfig Ref(AudioDevice d) => new() { Id = d.Id, FriendlyName = d.FriendlyName };
    private static ButtonActionConfig Act(ButtonActionType t) => new() { Action = t };

    /// <summary>One card per single-press action; the controller reports the given kinds
    /// (default: as many screens as cards).</summary>
    private static AppConfig Config(IndicatorKind[]? kinds, params ButtonActionConfig[] singlePresses)
    {
        var c = AppConfig.CreateDefault();
        c.Buttons.Clear();
        foreach (var a in singlePresses)
        {
            var b = new ButtonConfig();
            b.Default().SinglePress = a;
            c.Buttons.Add(b);
        }
        c.Controller = new ControllerInfoConfig
        {
            ProtocolVersion = 1, Sliders = 5,
            Buttons = (kinds ?? Enumerable.Repeat(IndicatorKind.Screen, singlePresses.Length)).ToList(),
        };
        return c;
    }

    private ScreenContent ScreenOf(AppConfig c, int button = 0, string preset = AppConfig.DefaultPresetId)
        => Assert.IsType<ScreenDisplay>(ControllerDisplay.Compute(c, preset, _audio)[button]).Content;

    private byte LedOf(AppConfig c, int button = 0, string preset = AppConfig.DefaultPresetId)
        => Assert.IsType<LedDisplay>(ControllerDisplay.Compute(c, preset, _audio)[button]).Value;

    private static IndicatorKind[] Leds(int n) => Enumerable.Repeat(IndicatorKind.Led, n).ToArray();

    // ── Screens (spec §2.2) ───────────────────────────────────────────────────

    [Fact]
    public void MuteCurrentOutput_ShowsMutedState_DefaultOutputsIconAndName()
    {
        var c = Config(null, Act(ButtonActionType.MuteCurrentOutput));
        _audio.DeviceStates["spk"] = (0.5f, false);
        Assert.Equal(ScreenContent.Text("LIVE", ScreenIcon.Speaker, false, "Speakers"), ScreenOf(c));
        _audio.DeviceStates["spk"] = (0.5f, true);
        Assert.Equal(ScreenContent.Text("MUTED", ScreenIcon.Speaker, true, "Speakers"), ScreenOf(c));
    }

    [Fact]
    public void MuteCurrentInput_UsesTheDefaultInput()
    {
        var c = Config(null, Act(ButtonActionType.MuteCurrentInput));
        _audio.DeviceStates["mic"] = (1f, true);
        Assert.Equal(ScreenContent.Text("MUTED", ScreenIcon.Mic, true, "Microphone"), ScreenOf(c));
    }

    [Fact]
    public void MuteCurrentOutput_NoDefaultDevice_ShowsNoDevice()
    {
        _audio.DefaultRender = null;
        var c = Config(Leds(1).Concat(new[] { IndicatorKind.Screen }).ToArray(),
                       Act(ButtonActionType.MuteCurrentOutput), Act(ButtonActionType.MuteCurrentOutput));
        Assert.Equal(0, LedOf(c, 0));
        Assert.Equal(ScreenContent.Text("", ScreenIcon.Generic, false, "No device"), ScreenOf(c, 1));
    }

    [Fact]
    public void Nickname_IsUsedOnTheScreen()
    {
        var c = Config(null, Act(ButtonActionType.MuteCurrentOutput));
        c.Devices.Add(new DeviceSettingsConfig { Id = "spk", Nickname = "Desk speakers", Icon = ScreenIcon.Speakers });
        Assert.Equal(ScreenContent.Text("LIVE", ScreenIcon.Speakers, false, "Desk speakers"), ScreenOf(c));
    }

    [Fact]
    public void MuteSpecificDevice_FollowsThatDevice_AndAsksForOneWhenUnset()
    {
        var c = Config(null,
            new ButtonActionConfig { Action = ButtonActionType.MuteSpecificDevice, Device = Ref(Headphones) },
            Act(ButtonActionType.MuteSpecificDevice));
        _audio.DeviceStates["hp"] = (1f, true);
        Assert.Equal(ScreenContent.Text("MUTED", ScreenIcon.Headphones, true, "Headphones"), ScreenOf(c, 0));
        Assert.Equal(ScreenContent.Text("", ScreenIcon.Generic, false, "Choose a device"), ScreenOf(c, 1));
    }

    [Fact]
    public void SetOutputDevice_ActiveOnlyWhileItIsTheOutput()
    {
        var c = Config(null,
            new ButtonActionConfig { Action = ButtonActionType.SetOutputDevice, Device = Ref(Speakers) },
            new ButtonActionConfig { Action = ButtonActionType.SetOutputDevice, Device = Ref(Monitor) });
        Assert.Equal(ScreenContent.Text("ACTIVE", ScreenIcon.Speaker, false, "Speakers"), ScreenOf(c, 0));
        Assert.Equal(ScreenContent.Text("", ScreenIcon.Display, false, "Monitor"), ScreenOf(c, 1));
    }

    [Fact]
    public void Toggle_ShowsTheActiveOutput_AndNamesTheOtherOnTop()
    {
        var c = Config(null, new ButtonActionConfig
        {
            Action = ButtonActionType.ToggleOutputDevice, Device = Ref(Speakers), DeviceB = Ref(Headphones),
        });
        Assert.Equal(ScreenContent.Text("NEXT: Headphones", ScreenIcon.Speaker, false, "Speakers"), ScreenOf(c));
        _audio.DefaultRender = Headphones;
        Assert.Equal(ScreenContent.Text("NEXT: Speakers", ScreenIcon.Headphones, false, "Headphones"), ScreenOf(c));
        _audio.DefaultRender = Monitor;   // neither: show A
        Assert.Equal(ScreenContent.Text("NEXT: Headphones", ScreenIcon.Speaker, false, "Speakers"), ScreenOf(c));
    }

    [Fact]
    public void Toggle_OneSideSet_UsesItForBoth_NoSidesAsksForADevice()
    {
        var c = Config(new[] { IndicatorKind.Screen, IndicatorKind.LedPair, IndicatorKind.Screen },
            new ButtonActionConfig { Action = ButtonActionType.ToggleOutputDevice, DeviceB = Ref(Speakers) },
            new ButtonActionConfig { Action = ButtonActionType.ToggleOutputDevice, DeviceB = Ref(Speakers) },
            Act(ButtonActionType.ToggleOutputDevice));
        Assert.Equal(ScreenContent.Text("NEXT: Speakers", ScreenIcon.Speaker, false, "Speakers"), ScreenOf(c, 0));
        Assert.Equal(1, LedOf(c, 1));
        Assert.Equal(ScreenContent.Text("", ScreenIcon.Generic, false, "Choose a device"), ScreenOf(c, 2));
    }

    [Fact]
    public void UnpluggedDevice_ShowsStoredNameAndIcon()
    {
        // Review Focus #2.
        var gone = new DeviceRefConfig { Id = "sony", FriendlyName = "Headphones (WH-1000XM4)" };
        var c = Config(new[] { IndicatorKind.Screen, IndicatorKind.Led },
            new ButtonActionConfig { Action = ButtonActionType.SetOutputDevice, Device = gone },
            new ButtonActionConfig { Action = ButtonActionType.MuteSpecificDevice, Device = gone });
        c.Devices.Add(new DeviceSettingsConfig { Id = "sony", Nickname = "Sony", Icon = ScreenIcon.Headphones });
        Assert.Equal(ScreenContent.Text("", ScreenIcon.Headphones, false, "Sony"), ScreenOf(c, 0));
        Assert.Equal(0, LedOf(c, 1));
    }

    [Fact]
    public void NextPreset_ShowsDotsAndTheLivePresetsName()
    {
        var c = Config(null, Act(ButtonActionType.NextPreset));
        c.Presets.Add(new PresetConfig { Id = "gaming", Name = "Gaming" });
        c.Buttons[0].GetOrAddBinding("gaming").SinglePress = Act(ButtonActionType.NextPreset);
        Assert.Equal(ScreenContent.Dots(0, 2, ScreenIcon.Presets, "Default"), ScreenOf(c));
        Assert.Equal(ScreenContent.Dots(1, 2, ScreenIcon.Presets, "Gaming"), ScreenOf(c, 0, "gaming"));
    }

    [Fact]
    public void GoToPreset_ShowsTheTargetsName()
    {
        var c = Config(null, new ButtonActionConfig { Action = ButtonActionType.GoToPreset, PresetId = "gaming" });
        c.Presets.Add(new PresetConfig { Id = "gaming", Name = "Gaming" });
        Assert.Equal(ScreenContent.Dots(0, 2, ScreenIcon.Presets, "Gaming"), ScreenOf(c));
    }

    [Fact]
    public void MoreThanTwelvePresets_TopShowsNOfN()
    {
        var c = Config(null, Act(ButtonActionType.NextPreset));
        for (int i = 2; i <= 13; i++) c.Presets.Add(new PresetConfig { Id = $"p{i}", Name = $"P{i}" });
        Assert.Equal(ScreenContent.Text("1/13", ScreenIcon.Presets, false, "Default"), ScreenOf(c));
    }

    [Fact]
    public void NotSetUp_IsABlankScreenAndAnLedOff()
    {
        var c = Config(new[] { IndicatorKind.Screen, IndicatorKind.Led }, Act(ButtonActionType.None), Act(ButtonActionType.None));
        Assert.Equal(ScreenContent.Blank, ScreenOf(c, 0));
        Assert.Equal(0, LedOf(c, 1));
    }

    [Fact]
    public void FirstActionThatIsNotNothing_Wins()
    {
        var c = Config(null, Act(ButtonActionType.None));
        c.Buttons[0].Default().DoublePress = Act(ButtonActionType.MuteCurrentInput);
        c.Buttons[0].Default().LongPress = Act(ButtonActionType.MuteCurrentOutput);
        Assert.Equal("Microphone", ScreenOf(c).Main);
    }

    // ── Controller vs config (spec §2.2) ──────────────────────────────────────

    [Fact]
    public void MoreControllerButtonsThanCards_ExtraButtonsAreNotSetUp()
    {
        // Review Focus #5.
        var c = Config(new[] { IndicatorKind.Screen, IndicatorKind.Screen, IndicatorKind.Led }, Act(ButtonActionType.MuteCurrentOutput));
        var all = ControllerDisplay.Compute(c, AppConfig.DefaultPresetId, _audio);
        Assert.Equal(3, all.Count);
        Assert.Equal(new ScreenDisplay(ScreenContent.Blank), all[1]);
        Assert.Equal(new LedDisplay(0), all[2]);
    }

    [Fact]
    public void FewerControllerButtonsThanCards_ExtraCardsIgnored()
    {
        var c = Config(new[] { IndicatorKind.Led }, Act(ButtonActionType.None), Act(ButtonActionType.MuteCurrentOutput));
        Assert.Single(ControllerDisplay.Compute(c, AppConfig.DefaultPresetId, _audio));
    }

    [Fact]
    public void KindNoneOrUnknown_IsNull_NoControllerInfo_IsEmpty()
    {
        var c = Config(new[] { IndicatorKind.None, (IndicatorKind)9 }, Act(ButtonActionType.MuteCurrentOutput), Act(ButtonActionType.MuteCurrentOutput));
        Assert.All(ControllerDisplay.Compute(c, AppConfig.DefaultPresetId, _audio), Assert.Null);
        c.Controller = null;
        Assert.Empty(ControllerDisplay.Compute(c, AppConfig.DefaultPresetId, _audio));
    }

    // ── LED values (the rules the old 0x05 feedback had) ─────────────────────────────

    [Fact]
    public void LedValues_FollowTheLivePreset()
    {
        var c = Config(Leds(4),
            Act(ButtonActionType.MuteCurrentOutput),
            new ButtonActionConfig { Action = ButtonActionType.SetOutputDevice, Device = Ref(Headphones) },
            new ButtonActionConfig { Action = ButtonActionType.ToggleOutputDevice, Device = Ref(Speakers), DeviceB = Ref(Headphones) },
            Act(ButtonActionType.NextPreset));
        c.Presets.Add(new PresetConfig { Id = "gaming", Name = "Gaming" });
        c.Buttons[3].GetOrAddBinding("gaming").SinglePress = Act(ButtonActionType.NextPreset);
        _audio.DeviceStates["spk"] = (1f, true);

        Assert.Equal(new byte[] { 1, 0, 1, 1 },
            ControllerDisplay.Compute(c, AppConfig.DefaultPresetId, _audio).Select(d => ((LedDisplay)d!).Value));
        _audio.DefaultRender = Headphones;
        Assert.Equal(1, LedOf(c, 1));
        Assert.Equal(2, LedOf(c, 2));
        Assert.Equal(0, LedOf(c, 0, "gaming"));   // nothing in Gaming: the light goes off
        Assert.Equal(2, LedOf(c, 3, "gaming"));
    }

    [Fact]
    public void ToggleOutputDevice_MatchesAFriendlyNameOnlyDevice()
    {
        var c = Config(Leds(1), new ButtonActionConfig
        {
            Action = ButtonActionType.ToggleOutputDevice,
            Device = new DeviceRefConfig { Id = "", FriendlyName = "Speakers (Realtek)" },
            DeviceB = Ref(Headphones),
        });
        Assert.Equal(1, LedOf(c));
    }
}
