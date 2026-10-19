using Google.Protobuf;
using Volco.Core.Controller.Proto;

namespace Volco.Core.Controller;

public enum DraftHintKind
{
    /// <summary>A button on GPIO 34–39: "This pin has no internal pull-up; add a resistor to 3.3 V."</summary>
    ButtonNoPullUp,
    /// <summary>A slider on ADC2 while UDP is off: "This pin can't be read if you enable Wi-Fi."</summary>
    SliderAdc2,
}

/// <param name="Index">The slider or button index.</param>
public sealed record DraftHint(DraftHintKind Kind, int Index);

/// <summary>Pure helpers for the Controller page's draft (spec §8.7). Inputs are numbered from 0,
/// like the Sliders and Buttons pages.</summary>
public static class ControllerDraft
{
    /// <summary>The I2C buses some button's screen uses, ascending; buses outside 0–3 are left out
    /// (the validator reports them).</summary>
    public static IReadOnlyList<int> UsedBuses(ControllerConfig c)
        => c.Buttons.Where(b => b.IndicatorCase == Button.IndicatorOneofCase.Screen)
                    .Select(b => (int)b.Screen.Bus).Where(bus => bus is >= 0 and <= 3)
                    .Distinct().Order().ToList();

    /// <summary>The I2C bus message for <paramref name="bus"/> (0–3), or null when absent.</summary>
    public static I2cBus? Bus(ControllerConfig c, int bus) => bus switch
    {
        0 => c.I2C0, 1 => c.I2C1, 2 => c.I2C2, 3 => c.I2C3, _ => null,
    };

    /// <summary>Sets the I2C bus message for <paramref name="bus"/> (0–3).</summary>
    public static void SetBus(ControllerConfig c, int bus, I2cBus? value)
    {
        switch (bus)
        {
            case 0: c.I2C0 = value; break;
            case 1: c.I2C1 = value; break;
            case 2: c.I2C2 = value; break;
            case 3: c.I2C3 = value; break;
        }
    }

    /// <summary>Any slider, button or I2C bus: choosing a preset would replace something.</summary>
    public static bool HasHardware(ControllerConfig c)
        => c.Sliders.Count > 0 || c.Buttons.Count > 0 || c.I2C0 != null || c.I2C1 != null || c.I2C2 != null || c.I2C3 != null;

    /// <summary>The preset <paramref name="draft"/>'s <c>preset</c> field names, if it is one we know.</summary>
    public static ControllerPreset? BasedOn(ControllerConfig draft)
        => draft.HasPreset ? ControllerPresets.All.FirstOrDefault(p => p.Id == draft.Preset && p.Hardware is not null) : null;

    /// <summary>The draft's hardware fields (sliders, buttons, i2c0..i2c3) encode differently
    /// from the preset's.</summary>
    public static bool IsModified(ControllerConfig draft, ControllerPreset preset)
        => preset.Hardware is null || !HardwareBytes(draft).SequenceEqual(HardwareBytes(preset.Hardware));

    private static byte[] HardwareBytes(ControllerConfig c)
    {
        var hw = new ControllerConfig();
        hw.Sliders.AddRange(c.Sliders);
        hw.Buttons.AddRange(c.Buttons);
        hw.I2C0 = c.I2C0;
        hw.I2C1 = c.I2C1;
        hw.I2C2 = c.I2C2;
        hw.I2C3 = c.I2C3;
        return hw.ToByteArray();
    }

    /// <summary>Informational hints, never blocking (spec §8.7).</summary>
    public static IReadOnlyList<DraftHint> Hints(ControllerConfig c, ChipInfo chip)
    {
        var hints = new List<DraftHint>();
        bool udp = c.Udp is { Enabled: true };
        for (int i = 0; i < c.Sliders.Count; i++)
            if (!udp && c.Sliders[i].HasPin && chip.IsAdc2(c.Sliders[i].Pin))
                hints.Add(new DraftHint(DraftHintKind.SliderAdc2, i));
        for (int i = 0; i < c.Buttons.Count; i++)
            if (c.Buttons[i].HasPin && c.Buttons[i].Pin is >= 34 and <= 39)
                hints.Add(new DraftHint(DraftHintKind.ButtonNoPullUp, i));
        return hints;
    }

    /// <summary>What a field code means, for "The controller rejected the setup: &lt;field&gt; on &lt;target&gt;."</summary>
    public static string FieldDescription(ConfigField field) => field switch
    {
        ConfigField.SliderPin => "the slider pin",
        ConfigField.ButtonPin => "the button pin",
        ConfigField.LedPin => "the LED pin",
        ConfigField.PairPinA => "LED pin A",
        ConfigField.PairPinB => "LED pin B",
        ConfigField.ScreenBus => "the screen bus",
        ConfigField.ScreenAddress => "the screen address",
        ConfigField.I2cMissing => "missing bus pins",
        ConfigField.I2cPins => "the SDA/SCL pins",
        ConfigField.I2cClock => "the clock speed",
        ConfigField.UdpSsid => "the network name",
        ConfigField.UdpPassword => "the password",
        ConfigField.UdpPcIp => "the PC IP address",
        ConfigField.UdpPort => "a port number",
        ConfigField.ScreenValue => "a value",
        ConfigField.InputValue => "a value",
        _ => "a setting",
    };

    /// <summary>Where a violation is: "Slider 1", "Button 2", "I2C bus 0", "Wi-Fi", "Screen
    /// behaviour" or "Input tuning" (spec §8.7). Sliders and buttons are numbered from 1, as
    /// everywhere a person reads them; <paramref name="item"/> is the 0-based input. I2C buses keep
    /// their hardware numbers (0-3).</summary>
    public static string Target(byte item, ConfigField field) => field switch
    {
        ConfigField.SliderPin => item == ValidationResult.NoItem ? "Sliders" : $"Slider {item + 1}",
        ConfigField.ButtonPin or ConfigField.LedPin or ConfigField.PairPinA or ConfigField.PairPinB
            or ConfigField.ScreenBus or ConfigField.ScreenAddress
            => item == ValidationResult.NoItem ? "Buttons" : $"Button {item + 1}",
        ConfigField.I2cMissing or ConfigField.I2cPins or ConfigField.I2cClock => $"I2C bus {item}",
        ConfigField.UdpSsid or ConfigField.UdpPassword or ConfigField.UdpPcIp or ConfigField.UdpPort => "Wi-Fi",
        ConfigField.ScreenValue => "Screen behaviour",
        ConfigField.InputValue => "Input tuning",
        _ => "the setup",
    };

    /// <summary>"&lt;field&gt; on &lt;target&gt;", e.g. "the LED pin on Button 2".</summary>
    public static string Describe(byte item, byte field)
        => $"{FieldDescription((ConfigField)field)} on {Target(item, (ConfigField)field)}";
}
