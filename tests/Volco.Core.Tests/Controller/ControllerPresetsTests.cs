using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Xunit;

namespace Volco.Core.Tests.Controller;

public class ControllerPresetsTests
{
    private static ControllerPreset Diy => ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId);
    private static ControllerPreset Pcb => ControllerPresets.All.Single(p => p.Id == ControllerPresets.VolcoPcbId);

    [Fact]
    public void All_ListsDiyThenThePcbThenBlank()
    {
        Assert.Equal(new[] { "diy-reference", "volco-pcb", "blank" }, ControllerPresets.All.Select(p => p.Id));
        Assert.All(ControllerPresets.All, p => Assert.True(p.IsAvailable));
        Assert.Equal("Volco PCB", Pcb.DisplayName);
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

    // The Volco PCB's pin map (deej-board repo, boards/5-volco-smd/hw/volco_pinout.py): SLIDER 1-6,
    // BUTTON 1-4, and DISPLAY n (one 0x3C screen per bus) on BUTTON n.
    [Fact]
    public void Pcb_MatchesTheBoardsPinMap()
    {
        var h = Pcb.Hardware!;
        Assert.Equal("volco-pcb", h.Preset);
        Assert.Equal(new uint[] { 33, 32, 35, 34, 39, 36 }, h.Sliders.Select(s => s.Pin));
        Assert.Equal(new uint[] { 13, 27, 26, 25 }, h.Buttons.Select(b => b.Pin));
        Assert.Equal(new[] { (0u, 0x3Cu), (1u, 0x3Cu), (2u, 0x3Cu), (3u, 0x3Cu) },
            h.Buttons.Select(b => (b.Screen.Bus, b.Screen.Address)));
        Assert.Equal((16u, 4u), (h.I2C0.Sda, h.I2C0.Scl));
        Assert.Equal((18u, 17u), (h.I2C1.Sda, h.I2C1.Scl));
        Assert.Equal((21u, 19u), (h.I2C2.Sda, h.I2C2.Scl));
        Assert.Equal((23u, 22u), (h.I2C3.Sda, h.I2C3.Scl));
        Assert.Null(h.Screens);
        Assert.Null(h.Udp);
    }

    [Fact]
    public void Pcb_PassesValidation()
        => Assert.True(ControllerConfigValidator.Validate(Pcb.Hardware!, ChipInfo.Esp32).IsOk);

    [Fact]
    public void ApplyTo_ReplacesHardwareAndKeepsEverythingElse()
    {
        var draft = new ControllerConfig
        {
            Preset = "blank",
            Screens = new ScreenBehaviour { Brightness = 10 },
            Input = new InputTuning { DebounceMs = 50 },
            Udp = new Udp { Enabled = true, Ssid = "net" },
            I2C2 = new I2cBus { Sda = 16, Scl = 17 },  // hardware: replaced (the DIY preset has no bus 2)
        };
        draft.Sliders.Add(new Slider { Pin = 39 });

        var result = ControllerPresets.ApplyTo(draft, Diy);

        Assert.Equal(new uint[] { 33, 35, 32, 36, 34 }, result.Sliders.Select(s => s.Pin));
        Assert.Equal(3, result.Buttons.Count);
        Assert.Equal("diy-reference", result.Preset);
        Assert.Equal(10u, result.Screens.Brightness);
        Assert.Equal(50u, result.Input.DebounceMs);
        Assert.Equal("net", result.Udp.Ssid);
        Assert.Null(result.I2C2);
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
        => Assert.Throws<InvalidOperationException>(() =>
            ControllerPresets.ApplyTo(new ControllerConfig(), new ControllerPreset("later", "Later", false, null)));

    [Fact]
    public void Hardware_IsACopy_EditingItLeavesThePresetAlone()
    {
        var diy = ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId);
        diy.Hardware!.Sliders[0].Pin = 39;
        Assert.Equal(33u, diy.Hardware!.Sliders[0].Pin);
        Assert.NotSame(diy.Hardware, diy.Hardware);
    }
}
