using System.Text;
using Volco.Core.Controller.Proto;

namespace Volco.Core.Controller;

/// <summary>Spec §6.4, step by step and in the same order as the firmware's validateConfig, so both
/// report the same first violation. The firmware's verdict is the one that counts; this gives the
/// user immediate feedback.</summary>
public static class ControllerConfigValidator
{
    private const byte None = ValidationResult.NoItem;

    /// <summary>I2C buses 0 and 1 are the ESP32's controllers; 2 and 3 are software I2C.</summary>
    public const int I2cBuses = 4;

    private static I2cBus? Bus(ControllerConfig c, int bus) => bus switch
    {
        0 => c.I2C0,
        1 => c.I2C1,
        2 => c.I2C2,
        _ => c.I2C3,
    };

    public static ValidationResult Validate(ControllerConfig c, ChipInfo chip)
    {
        // 1. Counts (decode already enforces them; reachable only for configs built in code).
        if (c.Sliders.Count > ControllerConfigCodec.MaxSliders) return ValidationResult.Invalid(None, ConfigField.SliderPin);
        if (c.Buttons.Count > ControllerConfigCodec.MaxButtons) return ValidationResult.Invalid(None, ConfigField.ButtonPin);
        var udp = c.EffectiveUdp();

        // 2. Sliders.
        for (var i = 0; i < c.Sliders.Count; i++)
        {
            var s = c.Sliders[i];
            if (!s.HasPin || !chip.AdcCapable(s.Pin)) return ValidationResult.Invalid((byte)i, ConfigField.SliderPin);
            if (udp.Enabled && chip.IsAdc2(s.Pin)) return ValidationResult.Invalid((byte)i, ConfigField.SliderPin);
        }

        // 3. Buttons: pin, indicator fields, then the (bus, address) duplicate check, one button at a time.
        for (var i = 0; i < c.Buttons.Count; i++)
        {
            var b = c.Buttons[i];
            var item = (byte)i;
            if (!b.HasPin || !chip.Usable(b.Pin)) return ValidationResult.Invalid(item, ConfigField.ButtonPin);
            switch (b.IndicatorCase)
            {
                case Button.IndicatorOneofCase.Led:
                    if (!b.Led.HasPin || !chip.OutputCapable(b.Led.Pin)) return ValidationResult.Invalid(item, ConfigField.LedPin);
                    break;
                case Button.IndicatorOneofCase.LedPair:
                    var p = b.LedPair;
                    if (!p.HasPinA || !chip.OutputCapable(p.PinA)) return ValidationResult.Invalid(item, ConfigField.PairPinA);
                    if (!p.HasPinB || !chip.OutputCapable(p.PinB) || p.PinB == p.PinA)
                        return ValidationResult.Invalid(item, ConfigField.PairPinB);
                    break;
                case Button.IndicatorOneofCase.Screen:
                    var screen = b.Screen;
                    if (screen.Bus >= I2cBuses) return ValidationResult.Invalid(item, ConfigField.ScreenBus);
                    if (screen.Address != 0x3C && screen.Address != 0x3D) return ValidationResult.Invalid(item, ConfigField.ScreenAddress);
                    for (var j = 0; j < i; j++)
                    {
                        var o = c.Buttons[j];
                        if (o.IndicatorCase == Button.IndicatorOneofCase.Screen && o.Screen.Bus == screen.Bus
                            && o.Screen.Address == screen.Address)
                            return ValidationResult.Invalid(item, ConfigField.ScreenAddress);
                    }
                    break;
            }
        }

        // 4. I2C, for each bus some screen uses (an unused I2cBus is ignored entirely).
        var used = new bool[I2cBuses];
        foreach (var b in c.Buttons)
            if (b.IndicatorCase == Button.IndicatorOneofCase.Screen) used[b.Screen.Bus] = true;
        for (byte bus = 0; bus < I2cBuses; bus++)
        {
            if (!used[bus]) continue;
            var i2c = Bus(c, bus);
            if (i2c is null) return ValidationResult.Invalid(bus, ConfigField.I2cMissing);
            if (!i2c.HasSda || !i2c.HasScl || !chip.OutputCapable(i2c.Sda) || !chip.OutputCapable(i2c.Scl) || i2c.Sda == i2c.Scl)
                return ValidationResult.Invalid(bus, ConfigField.I2cPins);
            if (i2c.ClockHz is < 100000 or > 1000000) return ValidationResult.Invalid(bus, ConfigField.I2cClock);
        }

        // 5. Pin uniqueness, in walk order: sliders, buttons with their indicator pins, used I2C buses.
        var seen = new HashSet<uint>();
        for (var i = 0; i < c.Sliders.Count; i++)
            if (!seen.Add(c.Sliders[i].Pin)) return ValidationResult.Invalid((byte)i, ConfigField.SliderPin);
        for (var i = 0; i < c.Buttons.Count; i++)
        {
            var b = c.Buttons[i];
            var item = (byte)i;
            if (!seen.Add(b.Pin)) return ValidationResult.Invalid(item, ConfigField.ButtonPin);
            if (b.IndicatorCase == Button.IndicatorOneofCase.Led && !seen.Add(b.Led.Pin))
                return ValidationResult.Invalid(item, ConfigField.LedPin);
            if (b.IndicatorCase == Button.IndicatorOneofCase.LedPair)
            {
                if (!seen.Add(b.LedPair.PinA)) return ValidationResult.Invalid(item, ConfigField.PairPinA);
                if (!seen.Add(b.LedPair.PinB)) return ValidationResult.Invalid(item, ConfigField.PairPinB);
            }
        }
        for (byte bus = 0; bus < I2cBuses; bus++)
        {
            if (!used[bus]) continue;
            var i2c = Bus(c, bus)!;
            if (!seen.Add(i2c.Sda) || !seen.Add(i2c.Scl)) return ValidationResult.Invalid(bus, ConfigField.I2cPins);
        }

        // 6. UDP, only when enabled (lengths are UTF-8 bytes, as on the firmware).
        if (udp.Enabled)
        {
            var ssid = Encoding.UTF8.GetByteCount(udp.Ssid);
            var password = Encoding.UTF8.GetByteCount(udp.Password);
            if (ssid is < 1 or > 32) return ValidationResult.Invalid(None, ConfigField.UdpSsid);
            if (password != 0 && password is < 8 or > 64) return ValidationResult.Invalid(None, ConfigField.UdpPassword);
            if (!udp.HasPcIp || BadIp(udp.PcIp)) return ValidationResult.Invalid(None, ConfigField.UdpPcIp);
            if (udp.PcPort is < 1 or > 65535 || udp.LocalPort is < 1 or > 65535) return ValidationResult.Invalid(None, ConfigField.UdpPort);
        }

        // 7. Ranges, in field-number order, screens before input (absent submessages = defaults).
        var s7 = c.EffectiveScreens();
        if (s7.Brightness > 255) return ValidationResult.Invalid(1, ConfigField.ScreenValue);
        if (s7.TransitionMs > 2000) return ValidationResult.Invalid(2, ConfigField.ScreenValue);
        if (s7.ScrollHoldMs > 10000) return ValidationResult.Invalid(3, ConfigField.ScreenValue);
        if (s7.ScrollSpeedPxS is < 5 or > 200) return ValidationResult.Invalid(4, ConfigField.ScreenValue);
        if (s7.ScrollEndHoldMs > 10000) return ValidationResult.Invalid(5, ConfigField.ScreenValue);
        if (s7.DimAfterS is < 10 or > 86400) return ValidationResult.Invalid(7, ConfigField.ScreenValue);
        if (s7.DimBrightness > 255) return ValidationResult.Invalid(8, ConfigField.ScreenValue);
        if (s7.OffAfterS is < 10 or > 86400) return ValidationResult.Invalid(10, ConfigField.ScreenValue);
        if (s7.PixelShiftEveryS is < 10 or > 86400) return ValidationResult.Invalid(12, ConfigField.ScreenValue);
        var input = c.EffectiveInput();
        if (input.DebounceMs is < 1 or > 200) return ValidationResult.Invalid(1, ConfigField.InputValue);
        if (input.SliderIntervalMs is < 1 or > 100) return ValidationResult.Invalid(2, ConfigField.InputValue);
        if (input.FilterWeightPct is < 1 or > 100) return ValidationResult.Invalid(3, ConfigField.InputValue);
        if (input.SliderDeadband > 500) return ValidationResult.Invalid(4, ConfigField.InputValue);

        return ValidationResult.Ok;
    }

    private static bool BadIp(uint ip) => ip == 0 || ip == 0xFFFFFFFF || ip >> 24 == 127 || ip >> 28 == 0xE;
}
