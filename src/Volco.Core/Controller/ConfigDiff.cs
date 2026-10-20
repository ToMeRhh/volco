using Volco.Core.Controller.Proto;

namespace Volco.Core.Controller;

/// <summary>
/// What tells two controller setups apart, in the words of Compare (hardware-page spec §7.2): only
/// the lines that differ, each describing one side. The counts ("4 sliders, 3 buttons, 1 screen"),
/// a control whose pin moved ("Slider 2 on GPIO 35"), a light or screen that moved, an I2C bus that
/// moved, and Wi-Fi ("Wi-Fi on (HomeNet)", "Wi-Fi off"). Controls are numbered from 1.
/// </summary>
public static class ConfigDiff
{
    /// <summary>The lines of <paramref name="theirs"/> (when <paramref name="forTheirs"/>) or of
    /// <paramref name="mine"/> that differ from the other setup; empty when they are the same.</summary>
    public static IReadOnlyList<string> Lines(ControllerConfig mine, ControllerConfig theirs, bool forTheirs)
    {
        var side = forTheirs ? theirs : mine;
        var other = forTheirs ? mine : theirs;
        var lines = new List<string>();

        var counts = Counts(side);
        if (counts != Counts(other)) lines.Add(counts);

        for (int i = 0; i < Math.Min(side.Sliders.Count, other.Sliders.Count); i++)
        {
            var s = side.Sliders[i];
            var o = other.Sliders[i];
            if (s.HasPin != o.HasPin || s.Pin != o.Pin) lines.Add($"Slider {i + 1} {On(s.HasPin, s.Pin)}");
        }

        for (int i = 0; i < Math.Min(side.Buttons.Count, other.Buttons.Count); i++)
        {
            var b = side.Buttons[i];
            var o = other.Buttons[i];
            if (b.HasPin != o.HasPin || b.Pin != o.Pin) lines.Add($"Button {i + 1} {On(b.HasPin, b.Pin)}");
            if (!SameIndicator(b, o)) lines.Add(Indicator(i, b));
        }

        for (int bus = 0; bus < 4; bus++)
        {
            var s = ControllerDraft.Bus(side, bus);
            var o = ControllerDraft.Bus(other, bus);
            if (s is null || Equals(s, o)) continue;
            lines.Add($"I2C bus {bus} on SDA {Pin(s.HasSda, s.Sda)}, SCL {Pin(s.HasScl, s.Scl)}");
        }

        var wifi = Wifi(side);
        if (wifi != Wifi(other)) lines.Add(wifi);
        return lines;
    }

    /// <summary>"5 sliders, 4 buttons", with ", 1 screen" when any button has a screen.</summary>
    public static string Counts(ControllerConfig config)
    {
        var screens = config.Buttons.Count(b => b.IndicatorCase == Button.IndicatorOneofCase.Screen);
        var text = $"{Plural(config.Sliders.Count, "slider")}, {Plural(config.Buttons.Count, "button")}";
        return screens > 0 ? $"{text}, {Plural(screens, "screen")}" : text;
    }

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    private static string On(bool has, uint pin) => has ? $"on GPIO {pin}" : "has no pin";

    private static string Pin(bool has, uint pin) => has ? $"{pin}" : "–";

    private static bool SameIndicator(Button a, Button b) => a.IndicatorCase == b.IndicatorCase && a.IndicatorCase switch
    {
        Button.IndicatorOneofCase.Led => a.Led.HasPin == b.Led.HasPin && a.Led.Pin == b.Led.Pin,
        Button.IndicatorOneofCase.LedPair => a.LedPair.HasPinA == b.LedPair.HasPinA && a.LedPair.PinA == b.LedPair.PinA
                                             && a.LedPair.HasPinB == b.LedPair.HasPinB && a.LedPair.PinB == b.LedPair.PinB,
        Button.IndicatorOneofCase.Screen => a.Screen.Bus == b.Screen.Bus && a.Screen.Address == b.Screen.Address,
        _ => true,
    };

    private static string Indicator(int i, Button b) => b.IndicatorCase switch
    {
        Button.IndicatorOneofCase.Led => $"Button {i + 1} light {On(b.Led.HasPin, b.Led.Pin)}",
        Button.IndicatorOneofCase.LedPair
            => $"Button {i + 1} lights on GPIO {Pin(b.LedPair.HasPinA, b.LedPair.PinA)} and {Pin(b.LedPair.HasPinB, b.LedPair.PinB)}",
        Button.IndicatorOneofCase.Screen => $"Button {i + 1} screen on I2C bus {b.Screen.Bus} at {b.Screen.Address:X2}",
        _ => $"Button {i + 1} has no light or screen",
    };

    private static string Wifi(ControllerConfig config)
    {
        var udp = config.EffectiveUdp();
        if (!udp.Enabled) return "Wi-Fi off";
        return string.IsNullOrEmpty(udp.Ssid) ? "Wi-Fi on" : $"Wi-Fi on ({udp.Ssid})";
    }
}
