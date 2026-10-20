using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Xunit;

namespace Volco.Core.Tests.Controller;

public class PinChoicesTests
{
    private static readonly ChipInfo Chip = ChipInfo.Esp32;
    private static readonly PinOwner Slider6 = new(PinOwnerKind.Slider, 5);

    private static ControllerConfig Diy() => ControllerPresets.ApplyTo(new ControllerConfig(),
        ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId));

    private static PinChoice At(ControllerConfig d, PinRole role, PinOwner self, uint pin)
        => PinChoices.For(d, Chip, role, self).Single(c => c.Pin == pin);

    [Fact]
    public void ListsEveryExistingPinInOrder()
    {
        var pins = PinChoices.For(Diy(), Chip, PinRole.SliderInput, Slider6).Select(c => c.Pin);
        Assert.Equal(Enumerable.Range(0, 40).Select(p => (uint)p).Where(Chip.Exists), pins);
    }

    [Fact]
    public void ReservedPinsAreUnusableWithTheirReason()
    {
        var d = Diy();
        Assert.Equal(("USB serial", PinChoiceState.Unusable), (At(d, PinRole.ButtonInput, Slider6, 1).Note, At(d, PinRole.ButtonInput, Slider6, 1).State));
        Assert.Equal(("flash", PinChoiceState.Unusable), (At(d, PinRole.ButtonInput, Slider6, 7).Note, At(d, PinRole.ButtonInput, Slider6, 7).State));
    }

    [Fact]
    public void SliderOnNonAdcPinIsUnusable()
    {
        var d = new ControllerConfig();
        var p23 = At(d, PinRole.SliderInput, Slider6, 23);
        Assert.Equal(PinChoiceState.Unusable, p23.State);
        Assert.Equal("no analog input", p23.Note);
        Assert.NotEqual(PinChoiceState.Unusable, At(d, PinRole.SliderInput, Slider6, 36).State);
    }

    [Theory]
    [InlineData(PinRole.LightOutput)]
    [InlineData(PinRole.BusSda)]
    public void LightOrBusOnInputOnlyPinIsUnusable(PinRole role)
    {
        var c = At(new ControllerConfig(), role, Slider6, 34);
        Assert.Equal(PinChoiceState.Unusable, c.State);
        Assert.Equal("input only", c.Note);
    }

    [Fact]
    public void TakenPinNamesItsOwner()
    {
        var d = new ControllerConfig();
        d.Sliders.Add(new Slider { Pin = 33 });
        var c = At(d, PinRole.SliderInput, Slider6, 33);
        Assert.Equal(PinChoiceState.Taken, c.State);
        Assert.Equal("Slider 1", c.Note);
        Assert.Equal(new PinOwner(PinOwnerKind.Slider, 0), c.Owner);
    }

    [Fact]
    public void SelfIsFreeWithNoNote()
    {
        var d = new ControllerConfig();
        for (var i = 0; i < 5; i++) d.Sliders.Add(new Slider { Pin = (uint)(12 + i) });
        d.Sliders.Add(new Slider { Pin = 36 });
        var c = At(d, PinRole.SliderInput, Slider6, 36);
        Assert.Equal(PinChoiceState.Free, c.State);
        Assert.Equal("", c.Note);
    }

    [Fact]
    public void HintsAreNotesOnFreePins()
    {
        var d = new ControllerConfig();
        var b = At(d, PinRole.ButtonInput, new PinOwner(PinOwnerKind.Button, 0), 35);
        Assert.Equal((PinChoiceState.Free, "no pull-up"), (b.State, b.Note));

        Assert.Equal((PinChoiceState.Free, "free"), Pair(At(d, PinRole.SliderInput, Slider6, 25)));
        d.Udp = new Udp { Enabled = true };
        Assert.Equal((PinChoiceState.Free, "not with Wi-Fi"), Pair(At(d, PinRole.SliderInput, Slider6, 25)));
    }

    private static (PinChoiceState, string) Pair(PinChoice c) => (c.State, c.Note);

    [Fact]
    public void LightPairAndBusOwnersAreLabelled()
    {
        var d = new ControllerConfig();
        d.Buttons.Add(new Button { Pin = 4 });
        d.Buttons.Add(new Button { Pin = 5, LedPair = new LedPair { PinA = 18, PinB = 19 } });
        d.I2C0 = new I2cBus { Sda = 25, Scl = 26 };
        Assert.Equal("Button 2 light B", At(d, PinRole.LightOutput, Slider6, 19).Note);
        Assert.Equal("Button 2 light A", At(d, PinRole.LightOutput, Slider6, 18).Note);
        Assert.Equal("I2C bus 0 SDA", At(d, PinRole.LightOutput, Slider6, 25).Note);
    }
}
