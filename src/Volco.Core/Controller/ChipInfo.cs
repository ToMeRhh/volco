namespace Volco.Core.Controller;

/// <summary>What each GPIO of a chip family can do (spec §6.4): facts about the chip, not user
/// preferences. Bit n of each mask is GPIO n; any pin of 64 or more is never usable.</summary>
public sealed class ChipInfo
{
    private readonly ulong _exists, _reserved, _inputOnly, _adc1, _adc2;

    private ChipInfo(ulong exists, ulong reserved, ulong inputOnly, ulong adc1, ulong adc2)
        => (_exists, _reserved, _inputOnly, _adc1, _adc2) = (exists, reserved, inputOnly, adc1, adc2);

    /// <summary>Classic ESP32: exists 0-19, 21-23, 25-27, 32-39; reserved 1, 3 (USB serial) and
    /// 6-11 (flash); input-only 34-39; ADC1 32-39; ADC2 0, 2, 4, 12-15, 25-27.</summary>
    public static ChipInfo Esp32 { get; } = new(
        Span(0, 19) | Span(21, 23) | Span(25, 27) | Span(32, 39),
        Bit(1) | Bit(3) | Span(6, 11),
        Span(34, 39),
        Span(32, 39),
        Bit(0) | Bit(2) | Bit(4) | Span(12, 15) | Span(25, 27));

    public bool Exists(uint pin) => Has(_exists, pin);
    public bool Usable(uint pin) => Has(_exists, pin) && !Has(_reserved, pin);
    public bool OutputCapable(uint pin) => Usable(pin) && !Has(_inputOnly, pin);
    public bool AdcCapable(uint pin) => Usable(pin) && (Has(_adc1, pin) || Has(_adc2, pin));
    public bool IsAdc2(uint pin) => Has(_adc2, pin);

    private static bool Has(ulong mask, uint pin) => pin < 64 && ((mask >> (int)pin) & 1) != 0;
    private static ulong Bit(int p) => 1UL << p;

    private static ulong Span(int a, int b)
    {
        ulong m = 0;
        for (var p = a; p <= b; p++) m |= Bit(p);
        return m;
    }
}
