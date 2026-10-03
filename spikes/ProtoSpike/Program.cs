// SPIKE ONLY. Prints one line per vector: what Google.Protobuf does with it. Read the CI log.
using Google.Protobuf;
using Volco.Core.ControllerConfig.Proto;

static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

static void Check(string name, Func<string> f)
{
    try { Console.WriteLine($"VECTOR {name}: {f()}"); }
    catch (Exception e) { Console.WriteLine($"VECTOR {name}: THREW {e.GetType().Name}: {e.Message}"); }
}

static ControllerConfig Parse(string hex) => ControllerConfig.Parser.ParseFrom(Hex(hex));

Check("empty", () =>
{
    var c = ControllerConfig.Parser.ParseFrom(Array.Empty<byte>());
    return $"ok; Screens is null={c.Screens is null}; new ScreenBehaviour().Brightness={new ScreenBehaviour().Brightness}";
});
Check("screen_default", () =>
{
    var b = Parse("12 02 22 00").Buttons[0];
    return $"ok; case={b.IndicatorCase}; bus={b.Screen.Bus}; address=0x{b.Screen.Address:X2}; HasAddress={b.Screen.HasAddress}";
});
Check("brightness_only", () =>
{
    var s = Parse("32 02 08 64").Screens;
    return $"ok; brightness={s.Brightness}; dim_enabled={s.DimEnabled}; transition={s.TransitionMs}";
});
Check("buttons33", () => $"ok; buttons={Parse(string.Concat(Enumerable.Repeat("1200", 33))).Buttons.Count}");
Check("ssid33", () => $"ok; ssid length={Parse("2A23" + "1221" + string.Concat(Enumerable.Repeat("61", 33))).Udp.Ssid.Length}");
Check("bad_utf8", () => $"ok; preset={Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(Parse("42 02 C3 28").Preset))}");
Check("nul_in_string", () => $"ok; preset length={Parse("42 03 61 00 62").Preset.Length}");
Check("u32_overflow", () => $"ok; pin={Parse("0A 06 08 80 80 80 80 10").Sliders[0].Pin}");
Check("unknown_field", () =>
{
    var c = Parse("98 06 01 32 02 08 64");
    return $"ok; brightness={c.Screens.Brightness}; re-encoded={Convert.ToHexString(c.ToByteArray())}";
});

static ControllerConfig Maximal()
{
    var m = new ControllerConfig { Preset = new string('p', 24) };
    for (var i = 0; i < 16; i++) m.Sliders.Add(new Slider { Pin = 39 });
    for (var i = 0; i < 32; i++)
        m.Buttons.Add(new Button { Pin = 39, LedPair = new LedPair { PinA = 39, PinB = 39, ActiveLow = true } });
    m.I2C0 = new I2cBus { Sda = 39, Scl = 39, ClockHz = 1000000 };
    m.I2C1 = new I2cBus { Sda = 39, Scl = 39, ClockHz = 1000000 };
    m.Udp = new Udp
    {
        Enabled = true, Ssid = new string('a', 32), Password = new string('a', 64),
        PcIp = 0xFFFFFFFFu, PcPort = 65535, LocalPort = 65535,
    };
    m.Screens = new ScreenBehaviour
    {
        Brightness = 255, TransitionMs = 2000, ScrollHoldMs = 10000, ScrollSpeedPxS = 200,
        ScrollEndHoldMs = 10000, DimEnabled = true, DimAfterS = 86400, DimBrightness = 255,
        OffEnabled = true, OffAfterS = 86400, PixelShiftEnabled = true, PixelShiftEveryS = 86400,
        WakeSwallowsPress = true,
    };
    m.Input = new InputTuning { DebounceMs = 200, SliderIntervalMs = 100, FilterWeightPct = 100, SliderDeadband = 500 };
    return m;
}

Check("maximal_size", () => $"{Maximal().CalculateSize()} bytes");
Check("determinism", () => $"equal={Maximal().ToByteArray().AsSpan().SequenceEqual(Maximal().ToByteArray())}");
