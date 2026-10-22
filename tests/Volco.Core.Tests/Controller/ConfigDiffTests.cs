using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Xunit;

namespace Volco.Core.Tests.Controller;

/// <summary>Hardware-page spec §7.2: Compare lists only the lines that differ, each for its side.</summary>
public class ConfigDiffTests
{
    private static ControllerConfig Sliders(params uint[] pins)
    {
        var c = new ControllerConfig();
        foreach (var pin in pins) c.Sliders.Add(new Slider { Pin = pin });
        c.Buttons.Add(new Button { Pin = 13, Led = new Led { Pin = 12 } });
        c.Buttons.Add(new Button { Pin = 14 });
        c.Buttons.Add(new Button { Pin = 15 });
        return c;
    }

    [Fact]
    public void ConfigDiffListsOnlyDifferences()
    {
        var mine = Sliders(32, 33, 34, 36);
        Assert.Empty(ConfigDiff.Lines(mine, mine.Clone(), forTheirs: false));
        Assert.Empty(ConfigDiff.Lines(mine, mine.Clone(), forTheirs: true));

        var five = Sliders(32, 33, 34, 36, 39);
        Assert.Equal(new[] { "4 sliders, 3 buttons" }, ConfigDiff.Lines(mine, five, forTheirs: false));
        Assert.Equal(new[] { "5 sliders, 3 buttons" }, ConfigDiff.Lines(mine, five, forTheirs: true));

        var moved = Sliders(32, 35, 34, 36);
        Assert.Equal(new[] { "Slider 2 on GPIO 35" }, ConfigDiff.Lines(mine, moved, forTheirs: true));
        Assert.Equal(new[] { "Slider 2 on GPIO 33" }, ConfigDiff.Lines(mine, moved, forTheirs: false));
    }

    [Fact]
    public void ConfigDiffListsAutoCalibrate()
    {
        var mine = Sliders(32, 33);
        var theirs = mine.Clone();
        theirs.Sliders[1].AutoCalibrate = false;
        Assert.Equal(new[] { "Slider 2 auto-calibrate off" }, ConfigDiff.Lines(mine, theirs, forTheirs: true));
        Assert.Equal(new[] { "Slider 2 auto-calibrate on" }, ConfigDiff.Lines(mine, theirs, forTheirs: false));
    }

    [Fact]
    public void ScreensLightsAndWifi()
    {
        var mine = Sliders(32);
        var theirs = mine.Clone();
        theirs.Buttons[1].Screen = new Screen { Bus = 0, Address = 0x3C };
        theirs.I2C0 = new I2cBus { Sda = 21, Scl = 22 };
        theirs.Buttons[0].Led.Pin = 2;
        theirs.Udp = new Udp { Enabled = true, Ssid = "HomeNet" };

        Assert.Equal(new[]
        {
            "1 slider, 3 buttons, 1 screen",
            "Button 1 light on GPIO 2",
            "Button 2 screen on I2C bus 0 at 3C",
            "I2C bus 0 on SDA 21, SCL 22",
            "Wi-Fi on (HomeNet)",
        }, ConfigDiff.Lines(mine, theirs, forTheirs: true));
        Assert.Equal(new[]
        {
            "1 slider, 3 buttons",
            "Button 1 light on GPIO 12",
            "Button 2 has no light or screen",
            "Wi-Fi off",
        }, ConfigDiff.Lines(mine, theirs, forTheirs: false));
    }

    [Fact]
    public void CountsSaySingularAndScreens()
    {
        var diy = ControllerPresets.ApplyTo(new ControllerConfig(),
                                            ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId));
        Assert.Equal("5 sliders, 3 buttons", ConfigDiff.Counts(diy));
        Assert.Equal("0 sliders, 0 buttons", ConfigDiff.Counts(new ControllerConfig()));
    }
}
