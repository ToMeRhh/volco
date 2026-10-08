using System.Text;
using Volco.Core.Controller.Proto;

namespace Volco.Core.Controller;

/// <summary>One thing wrong with a draft, on the field it belongs to. <see cref="Item"/> means
/// what <see cref="ValidationResult.Item"/> means: a slider or button index, an I2C bus, a range
/// field number, or <see cref="ValidationResult.NoItem"/>.</summary>
public sealed record DraftProblem(ConfigField Field, byte Item, string Message);

/// <summary>Every violation of spec §6.4 in a draft, each worded for the field it is on, so the
/// Controller page can mark every line involved (both ends of a pin conflict, say).
/// <see cref="ControllerConfigValidator"/> stays the firmware's twin and reports only the first;
/// this lists one exactly when that one fails, and always includes it.</summary>
public static class DraftProblems
{
    private const byte None = ValidationResult.NoItem;

    public static IReadOnlyList<DraftProblem> Find(ControllerConfig c, ChipInfo chip)
    {
        var problems = new List<DraftProblem>();
        void Add(ConfigField field, byte item, string message)
        {
            if (!problems.Any(p => p.Field == field && p.Item == item)) problems.Add(new DraftProblem(field, item, message));
        }

        // 1. Counts.
        if (c.Sliders.Count > ControllerConfigCodec.MaxSliders)
            Add(ConfigField.SliderPin, None, $"A controller can have at most {ControllerConfigCodec.MaxSliders} sliders.");
        if (c.Buttons.Count > ControllerConfigCodec.MaxButtons)
            Add(ConfigField.ButtonPin, None, $"A controller can have at most {ControllerConfigCodec.MaxButtons} buttons.");
        var udp = c.EffectiveUdp();

        // 2. Sliders.
        for (var i = 0; i < c.Sliders.Count; i++)
        {
            var s = c.Sliders[i];
            if (!s.HasPin) Add(ConfigField.SliderPin, (byte)i, "Choose the slider's pin.");
            else if (!chip.Usable(s.Pin)) Add(ConfigField.SliderPin, (byte)i, Unusable(chip, s.Pin));
            else if (!chip.AdcCapable(s.Pin))
                Add(ConfigField.SliderPin, (byte)i, $"Pin {s.Pin} can't read a slider. Use an analog pin ({Pins(chip.AdcCapable)}).");
            else if (udp.Enabled && chip.IsAdc2(s.Pin))
                Add(ConfigField.SliderPin, (byte)i, $"Pin {s.Pin} can't read a slider while Wi-Fi is on. Use one of pins {Pins(p => chip.AdcCapable(p) && !chip.IsAdc2(p))}.");
        }

        // 3. Buttons and their indicators.
        for (var i = 0; i < c.Buttons.Count; i++)
        {
            var b = c.Buttons[i];
            var item = (byte)i;
            if (!b.HasPin) Add(ConfigField.ButtonPin, item, "Choose the button's pin.");
            else if (!chip.Usable(b.Pin)) Add(ConfigField.ButtonPin, item, Unusable(chip, b.Pin));
            switch (b.IndicatorCase)
            {
                case Button.IndicatorOneofCase.Led:
                    if (Output(chip, b.Led.HasPin, b.Led.Pin, "the LED", "drive an LED") is { } led) Add(ConfigField.LedPin, item, led);
                    break;
                case Button.IndicatorOneofCase.LedPair:
                    var p = b.LedPair;
                    if (Output(chip, p.HasPinA, p.PinA, "LED A", "drive an LED") is { } a) Add(ConfigField.PairPinA, item, a);
                    if (Output(chip, p.HasPinB, p.PinB, "LED B", "drive an LED") is { } pb) Add(ConfigField.PairPinB, item, pb);
                    else if (p.HasPinA && p.PinB == p.PinA) Add(ConfigField.PairPinB, item, "LED A and LED B need different pins.");
                    break;
                case Button.IndicatorOneofCase.Screen:
                    var screen = b.Screen;
                    if (screen.Bus >= ControllerConfigValidator.I2cBuses) Add(ConfigField.ScreenBus, item, "Choose a bus from 0 to 3.");
                    else if (screen.Address != 0x3C && screen.Address != 0x3D)
                        Add(ConfigField.ScreenAddress, item, "Use the address 0x3C or 0x3D.");
                    else
                        for (var j = 0; j < c.Buttons.Count; j++)
                        {
                            var o = c.Buttons[j];
                            if (j != i && o.IndicatorCase == Button.IndicatorOneofCase.Screen && o.Screen.Bus == screen.Bus
                                && o.Screen.Address == screen.Address)
                            {
                                Add(ConfigField.ScreenAddress, item,
                                    $"Button {j}'s screen is also on bus {screen.Bus} at 0x{screen.Address:X2}. Give one of them the other address or another bus.");
                                break;
                            }
                        }
                    break;
            }
        }

        // 4. I2C, for each bus some screen uses.
        var used = new bool[ControllerConfigValidator.I2cBuses];
        foreach (var b in c.Buttons)
            if (b.IndicatorCase == Button.IndicatorOneofCase.Screen && b.Screen.Bus < used.Length) used[b.Screen.Bus] = true;
        for (byte bus = 0; bus < used.Length; bus++)
        {
            if (!used[bus]) continue;
            var i2c = ControllerDraft.Bus(c, bus);
            if (i2c is null || !i2c.HasSda || !i2c.HasScl)
            {
                Add(i2c is null ? ConfigField.I2cMissing : ConfigField.I2cPins, bus, "Choose the bus's SDA and SCL pins.");
                continue;
            }
            if (Output(chip, true, i2c.Sda, "SDA", "be SDA") is { } sda) Add(ConfigField.I2cPins, bus, sda);
            else if (Output(chip, true, i2c.Scl, "SCL", "be SCL") is { } scl) Add(ConfigField.I2cPins, bus, scl);
            else if (i2c.Sda == i2c.Scl) Add(ConfigField.I2cPins, bus, "SDA and SCL need different pins.");
            if (i2c.ClockHz is < 100000 or > 1000000) Add(ConfigField.I2cClock, bus, "Set a clock from 100 to 1000 kHz.");
        }

        // 5. Pin uniqueness: every use of a shared pin, on each field that uses it.
        var uses = new List<(uint Pin, ConfigField Field, byte Item, string Who)>();
        for (var i = 0; i < c.Sliders.Count; i++)
            if (c.Sliders[i].HasPin) uses.Add((c.Sliders[i].Pin, ConfigField.SliderPin, (byte)i, $"Slider {i}"));
        for (var i = 0; i < c.Buttons.Count; i++)
        {
            var b = c.Buttons[i];
            var item = (byte)i;
            if (b.HasPin) uses.Add((b.Pin, ConfigField.ButtonPin, item, $"Button {i}"));
            if (b.IndicatorCase == Button.IndicatorOneofCase.Led && b.Led.HasPin)
                uses.Add((b.Led.Pin, ConfigField.LedPin, item, $"Button {i}'s LED"));
            if (b.IndicatorCase == Button.IndicatorOneofCase.LedPair)
            {
                if (b.LedPair.HasPinA) uses.Add((b.LedPair.PinA, ConfigField.PairPinA, item, $"Button {i}'s LED A"));
                // A and B on one pin is the pair's own problem (above).
                if (b.LedPair.HasPinB && !(b.LedPair.HasPinA && b.LedPair.PinB == b.LedPair.PinA))
                    uses.Add((b.LedPair.PinB, ConfigField.PairPinB, item, $"Button {i}'s LED B"));
            }
        }
        for (byte bus = 0; bus < used.Length; bus++)
        {
            if (!used[bus] || ControllerDraft.Bus(c, bus) is not { HasSda: true, HasScl: true } i2c || i2c.Sda == i2c.Scl) continue;
            uses.Add((i2c.Sda, ConfigField.I2cPins, bus, $"I2C bus {bus}'s SDA"));
            uses.Add((i2c.Scl, ConfigField.I2cPins, bus, $"I2C bus {bus}'s SCL"));
        }
        foreach (var group in uses.GroupBy(u => u.Pin).Where(g => g.Count() > 1))
            foreach (var use in group)
            {
                var others = group.Where(o => o.Who != use.Who).Select(o => o.Who).ToList();
                Add(use.Field, use.Item, $"Pin {use.Pin} is also used by {Join(others)}. Each pin can only do one thing.");
            }

        // 6. Wi-Fi, only when enabled.
        if (udp.Enabled)
        {
            var ssid = Encoding.UTF8.GetByteCount(udp.Ssid);
            var password = Encoding.UTF8.GetByteCount(udp.Password);
            if (ssid is < 1 or > 32) Add(ConfigField.UdpSsid, None, "Enter the network name (up to 32 characters).");
            if (password != 0 && password is < 8 or > 64)
                Add(ConfigField.UdpPassword, None, "The password must be 8 to 64 characters, or empty for an open network.");
            if (!udp.HasPcIp || ControllerConfigValidator.BadIp(udp.PcIp))
                Add(ConfigField.UdpPcIp, None, "Enter this PC's address on the network, like 192.168.1.10.");
            if (udp.PcPort is < 1 or > 65535 || udp.LocalPort is < 1 or > 65535)
                Add(ConfigField.UdpPort, None, "Ports must be from 1 to 65535.");
        }

        // 7. Ranges.
        var s7 = c.EffectiveScreens();
        void Range(ConfigField field, byte item, bool bad, string message) { if (bad) Add(field, item, message); }
        Range(ConfigField.ScreenValue, 1, s7.Brightness > 255, "Brightness must be from 0 to 255.");
        Range(ConfigField.ScreenValue, 2, s7.TransitionMs > 2000, "The slide duration must be at most 2000 ms.");
        Range(ConfigField.ScreenValue, 3, s7.ScrollHoldMs > 10000, "The scroll hold must be at most 10000 ms.");
        Range(ConfigField.ScreenValue, 4, s7.ScrollSpeedPxS is < 5 or > 200, "The scroll speed must be from 5 to 200 px/s.");
        Range(ConfigField.ScreenValue, 5, s7.ScrollEndHoldMs > 10000, "The scroll end hold must be at most 10000 ms.");
        Range(ConfigField.ScreenValue, 7, s7.DimAfterS is < 10 or > 86400, "Dim after must be from 10 to 86400 s.");
        Range(ConfigField.ScreenValue, 8, s7.DimBrightness > 255, "The dimmed brightness must be from 0 to 255.");
        Range(ConfigField.ScreenValue, 10, s7.OffAfterS is < 10 or > 86400, "Turn off after must be from 10 to 86400 s.");
        Range(ConfigField.ScreenValue, 12, s7.PixelShiftEveryS is < 10 or > 86400, "Pixel shift must be from 10 to 86400 s.");
        var input = c.EffectiveInput();
        Range(ConfigField.InputValue, 1, input.DebounceMs is < 1 or > 200, "Debounce must be from 1 to 200 ms.");
        Range(ConfigField.InputValue, 2, input.SliderIntervalMs is < 1 or > 100, "The slider sample interval must be from 1 to 100 ms.");
        Range(ConfigField.InputValue, 3, input.FilterWeightPct is < 1 or > 100, "Smoothing must be from 1 to 100 %.");
        Range(ConfigField.InputValue, 4, input.SliderDeadband > 500, "The deadband must be at most 500.");

        // Whatever the validator reports is on the list, worded or not.
        var first = ControllerConfigValidator.Validate(c, chip);
        if (!first.IsOk && !problems.Any(p => p.Item == first.Item && (byte)p.Field == first.Field))
            problems.Add(new DraftProblem((ConfigField)first.Field, first.Item,
                $"Check {ControllerDraft.FieldDescription((ConfigField)first.Field)}."));
        return problems;
    }

    /// <summary>A missing pin, one the chip lacks or reserves, or an input-only one where an output
    /// is needed; null when the pin is fine.</summary>
    private static string? Output(ChipInfo chip, bool has, uint pin, string what, string job)
    {
        if (!has) return $"Choose {what}'s pin.";
        if (!chip.Usable(pin)) return Unusable(chip, pin);
        if (!chip.OutputCapable(pin)) return $"Pin {pin} is input-only, so it can't {job}.";
        return null;
    }

    private static string Unusable(ChipInfo chip, uint pin)
        => chip.Exists(pin) ? $"Pin {pin} is reserved on the ESP32 (USB serial or flash)." : $"The ESP32 has no pin {pin}.";

    /// <summary>"32–39", "0, 2, 4, 12–15, 25–27, 32–39".</summary>
    private static string Pins(Func<uint, bool> ok)
    {
        var parts = new List<string>();
        for (uint p = 0; p < 64; p++)
        {
            if (!ok(p)) continue;
            var end = p;
            while (end + 1 < 64 && ok(end + 1)) end++;
            parts.Add(end == p ? $"{p}" : end == p + 1 ? $"{p}, {end}" : $"{p}–{end}");
            p = end;
        }
        return string.Join(", ", parts);
    }

    private static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };
}
