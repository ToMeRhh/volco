using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Xunit;

namespace Volco.Core.Tests.Controller;

public class ControllerDraftTests
{
    private static ControllerPreset Diy => ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId);

    private static Button ScreenButton(uint bus) => new() { Pin = 4, Screen = new Screen { Bus = bus } };

    [Fact]
    public void UsedBuses_AscendingDistinct_OutOfRangeLeftOut()
    {
        var c = new ControllerConfig();
        c.Buttons.Add(ScreenButton(2));
        c.Buttons.Add(ScreenButton(0));
        c.Buttons.Add(ScreenButton(2));
        c.Buttons.Add(ScreenButton(7));
        c.Buttons.Add(new Button { Pin = 5, Led = new Led { Pin = 12 } });
        Assert.Equal(new[] { 0, 2 }, ControllerDraft.UsedBuses(c));
    }

    [Fact]
    public void IsModified_FalseForThePresetApplied_TrueAfterAPinChange()
    {
        var draft = new ControllerConfig { Screens = new ScreenBehaviour { Brightness = 40 }, Udp = new Udp { Enabled = true } };
        draft = ControllerPresets.ApplyTo(draft, Diy);
        Assert.False(ControllerDraft.IsModified(draft, Diy));
        Assert.Same(Diy, ControllerDraft.BasedOn(draft));

        draft.Sliders[0].Pin = 39;
        Assert.True(ControllerDraft.IsModified(draft, Diy));
    }

    [Fact]
    public void BasedOn_KnownAndUnknownNames()
    {
        Assert.Equal(ControllerPresets.BlankId, ControllerDraft.BasedOn(new ControllerConfig { Preset = "blank" })!.Id);
        Assert.Null(ControllerDraft.BasedOn(new ControllerConfig { Preset = "my-board" }));
        Assert.Null(ControllerDraft.BasedOn(new ControllerConfig()));
        Assert.Equal(ControllerPresets.VolcoPcbId, ControllerDraft.BasedOn(new ControllerConfig { Preset = "volco-pcb" })!.Id);
    }

    [Fact]
    public void HasHardware()
    {
        Assert.False(ControllerDraft.HasHardware(new ControllerConfig { Screens = new ScreenBehaviour() }));
        Assert.True(ControllerDraft.HasHardware(new ControllerConfig { I2C3 = new I2cBus() }));
        Assert.True(ControllerDraft.HasHardware(ControllerPresets.ApplyTo(new ControllerConfig(), Diy)));
    }

    [Fact]
    public void Hints_PullUpAndAdc2()
    {
        var c = new ControllerConfig();
        c.Sliders.Add(new Slider { Pin = 33 });
        c.Sliders.Add(new Slider { Pin = 25 });          // ADC2
        c.Buttons.Add(new Button { Pin = 4 });
        c.Buttons.Add(new Button { Pin = 35 });          // input-only, no pull-up
        Assert.Equal(new[] { new DraftHint(DraftHintKind.SliderAdc2, 1), new DraftHint(DraftHintKind.ButtonNoPullUp, 1) },
                     ControllerDraft.Hints(c, ChipInfo.Esp32));

        c.Udp = new Udp { Enabled = true };              // the validator reports ADC2 then
        Assert.Equal(new[] { new DraftHint(DraftHintKind.ButtonNoPullUp, 1) }, ControllerDraft.Hints(c, ChipInfo.Esp32));
    }

    [Theory]
    [InlineData(0, ConfigField.SliderPin, "the slider pin on Slider 1")]
    [InlineData(2, ConfigField.LedPin, "the LED pin on Button 3")]
    [InlineData(1, ConfigField.PairPinB, "LED pin B on Button 2")]
    [InlineData(3, ConfigField.ScreenAddress, "the screen address on Button 4")]
    [InlineData(0, ConfigField.I2cPins, "the SDA/SCL pins on I2C bus 0")]
    [InlineData(2, ConfigField.I2cMissing, "missing bus pins on I2C bus 2")]
    [InlineData(255, ConfigField.UdpPcIp, "the PC IP address on Wi-Fi")]
    [InlineData(1, ConfigField.ScreenValue, "a value on Screen behaviour")]
    [InlineData(4, ConfigField.InputValue, "a value on Input tuning")]
    [InlineData(255, ConfigField.SliderPin, "the slider pin on Sliders")]
    public void Describe(byte item, ConfigField field, string expected)
        => Assert.Equal(expected, ControllerDraft.Describe(item, (byte)field));

    [Fact]
    public void EveryFieldHasADescriptionAndATarget()
    {
        foreach (var field in Enum.GetValues<ConfigField>())
        {
            Assert.NotEqual("a setting", ControllerDraft.FieldDescription(field));
            Assert.NotEqual("the setup", ControllerDraft.Target(0, field));
        }
    }
}
