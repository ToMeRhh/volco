using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Xunit;

namespace Volco.Core.Tests.Controller;

public class DraftProblemsTests
{
    private static IReadOnlyList<DraftProblem> Find(ControllerConfig c) => DraftProblems.Find(c, ChipInfo.Esp32);

    [Fact]
    public void APinUsedTwice_MarksBothUses_AndNamesTheOther()
    {
        var c = new ControllerConfig();
        c.Sliders.Add(new Slider { Pin = 33 });
        c.Buttons.Add(new Button { Pin = 4, Led = new Led { Pin = 33 } });

        var problems = Find(c);

        Assert.Equal(2, problems.Count);
        Assert.Contains(new DraftProblem(ConfigField.SliderPin, 0, "Pin 33 is also used by Button 1's LED. Each pin can only do one thing."), problems);
        Assert.Contains(new DraftProblem(ConfigField.LedPin, 0, "Pin 33 is also used by Slider 1. Each pin can only do one thing."), problems);
    }

    [Fact]
    public void APinUsedThreeTimes_ListsBothOthers()
    {
        var c = new ControllerConfig();
        for (var i = 0; i < 3; i++) c.Buttons.Add(new Button { Pin = 4 });
        Assert.Equal("Pin 4 is also used by Button 2 and Button 3. Each pin can only do one thing.",
            Find(c).Single(p => p.Item == 0).Message);
    }

    [Fact]
    public void BadPins_SayWhy()
    {
        var c = new ControllerConfig();
        c.Sliders.Add(new Slider { Pin = 5 });
        c.Sliders.Add(new Slider());
        c.Buttons.Add(new Button { Pin = 7 });
        c.Buttons.Add(new Button { Pin = 20, Led = new Led { Pin = 35 } });
        c.Buttons.Add(new Button { Pin = 4, LedPair = new LedPair { PinA = 16, PinB = 16 } });

        var problems = Find(c);

        string On(ConfigField f, byte item) => problems.Single(p => p.Field == f && p.Item == item).Message;
        Assert.Equal("Pin 5 can't read a slider. Use an analog pin (0, 2, 4, 12–15, 25–27, 32–39).", On(ConfigField.SliderPin, 0));
        Assert.Equal("Choose the slider's pin.", On(ConfigField.SliderPin, 1));
        Assert.Equal("Pin 7 is reserved on the ESP32 (USB serial or flash).", On(ConfigField.ButtonPin, 0));
        Assert.Equal("The ESP32 has no pin 20.", On(ConfigField.ButtonPin, 1));
        Assert.Equal("Pin 35 is input-only, so it can't drive an LED.", On(ConfigField.LedPin, 1));
        Assert.Equal("LED A and LED B need different pins.", On(ConfigField.PairPinB, 2));
        Assert.DoesNotContain(problems, p => p.Field == ConfigField.PairPinA);   // A and B on one pin isn't a conflict too
    }

    [Fact]
    public void AnAdc2SliderWithWifi_PointsAtAdc1()
    {
        var c = new ControllerConfig { Udp = new Udp { Enabled = true, Ssid = "home", PcIp = 0xC0A8010A } };
        c.Sliders.Add(new Slider { Pin = 4 });
        Assert.Equal("Pin 4 can't read a slider while Wi-Fi is on. Use one of pins 32–39.",
            Find(c).Single(p => p.Field == ConfigField.SliderPin).Message);
    }

    [Fact]
    public void TwoScreensAtOneAddress_MarksBoth()
    {
        var c = new ControllerConfig { I2C0 = new I2cBus { Sda = 21, Scl = 22 } };
        c.Buttons.Add(new Button { Pin = 4, Screen = new Screen { Bus = 0, Address = 0x3C } });
        c.Buttons.Add(new Button { Pin = 5, Screen = new Screen { Bus = 0, Address = 0x3C } });

        var problems = Find(c);

        Assert.Equal(new byte[] { 0, 1 }, problems.Where(p => p.Field == ConfigField.ScreenAddress).Select(p => p.Item).ToArray());
        Assert.StartsWith("Button 2's screen is also on bus 0 at 0x3C.", problems[0].Message);
    }

    [Fact]
    public void AValidDraft_HasNone()
    {
        var c = ControllerPresets.ApplyTo(new ControllerConfig(),
            ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId));
        Assert.Empty(Find(c));
    }
}
