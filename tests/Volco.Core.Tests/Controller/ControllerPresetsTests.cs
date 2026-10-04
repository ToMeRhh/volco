using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Xunit;

namespace Volco.Core.Tests.Controller;

public class ControllerPresetsTests
{
    private static ControllerPreset Diy => ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId);

    [Fact]
    public void All_ListsDiyThenThePcbPlaceholderThenBlank()
    {
        Assert.Equal(new[] { "diy-reference", "volco-pcb", "blank" }, ControllerPresets.All.Select(p => p.Id));
        var pcb = ControllerPresets.All[1];
        Assert.False(pcb.IsAvailable);
        Assert.Null(pcb.Hardware);
        Assert.Equal("Volco PCB (coming soon)", pcb.DisplayName);
    }

    [Fact]
    public void Diy_MatchesTheSpecTable()
    {
        var h = Diy.Hardware!;
        Assert.Equal("diy-reference", h.Preset);
        Assert.Equal(new uint[] { 33, 35, 32, 36, 34 }, h.Sliders.Select(s => s.Pin));
        Assert.Equal(new uint[] { 4, 14, 5 }, h.Buttons.Select(b => b.Pin));
        Assert.Equal((12u, true), (h.Buttons[0].Led.Pin, h.Buttons[0].Led.ActiveLow));
        Assert.Equal((21u, true), (h.Buttons[1].Led.Pin, h.Buttons[1].Led.ActiveLow));
        Assert.Equal((18u, 19u, false), (h.Buttons[2].LedPair.PinA, h.Buttons[2].LedPair.PinB, h.Buttons[2].LedPair.ActiveLow));
        Assert.Equal((25u, 26u), (h.I2C0.Sda, h.I2C0.Scl));
        Assert.Equal((27u, 13u), (h.I2C1.Sda, h.I2C1.Scl));
        Assert.Null(h.Screens);
        Assert.Null(h.Udp);
    }

    [Fact]
    public void Diy_PassesValidation()
        => Assert.True(ControllerConfigValidator.Validate(Diy.Hardware!, ChipInfo.Esp32).IsOk);

    [Fact]
    public void ApplyTo_ReplacesHardwareAndKeepsEverythingElse()
    {
        var draft = new ControllerConfig
        {
            Preset = "blank",
            Screens = new ScreenBehaviour { Brightness = 10 },
            Input = new InputTuning { DebounceMs = 50 },
            Udp = new Udp { Enabled = true, Ssid = "net" },
        };
        draft.Sliders.Add(new Slider { Pin = 39 });

        var result = ControllerPresets.ApplyTo(draft, Diy);

        Assert.Equal(new uint[] { 33, 35, 32, 36, 34 }, result.Sliders.Select(s => s.Pin));
        Assert.Equal(3, result.Buttons.Count);
        Assert.Equal("diy-reference", result.Preset);
        Assert.Equal(10u, result.Screens.Brightness);
        Assert.Equal(50u, result.Input.DebounceMs);
        Assert.Equal("net", result.Udp.Ssid);
        Assert.Single(draft.Sliders);  // the draft itself is untouched
    }

    [Fact]
    public void ApplyTo_Blank_ClearsHardware()
    {
        var draft = ControllerPresets.ApplyTo(new ControllerConfig(), Diy);
        var blank = ControllerPresets.ApplyTo(draft, ControllerPresets.All.Single(p => p.Id == ControllerPresets.BlankId));
        Assert.Empty(blank.Sliders);
        Assert.Empty(blank.Buttons);
        Assert.Null(blank.I2C0);
        Assert.Null(blank.I2C1);
        Assert.Equal("blank", blank.Preset);
    }

    [Fact]
    public void ApplyTo_UnavailablePreset_Throws()
        => Assert.Throws<InvalidOperationException>(() => ControllerPresets.ApplyTo(new ControllerConfig(), ControllerPresets.All[1]));
}
