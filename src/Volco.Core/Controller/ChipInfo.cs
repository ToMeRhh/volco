namespace Volco.Core.Controller;

/// <summary>What each GPIO of a chip family can do (spec §6.4): facts about the chip, not user
/// preferences. Bit n of each mask is GPIO n; any pin of 64 or more is never usable. The tables are
/// generated from proto/chips.json (ChipInfo.g.cs).</summary>
public sealed partial class ChipInfo
{
    private readonly ulong _exists, _reserved, _inputOnly, _adc1, _adc2;

    private ChipInfo(ulong exists, ulong reserved, ulong inputOnly, ulong adc1, ulong adc2)
        => (_exists, _reserved, _inputOnly, _adc1, _adc2) = (exists, reserved, inputOnly, adc1, adc2);

    public bool Exists(uint pin) => Has(_exists, pin);
    public bool Usable(uint pin) => Has(_exists, pin) && !Has(_reserved, pin);
    public bool OutputCapable(uint pin) => Usable(pin) && !Has(_inputOnly, pin);
    public bool AdcCapable(uint pin) => Usable(pin) && (Has(_adc1, pin) || Has(_adc2, pin));
    public bool IsAdc2(uint pin) => Has(_adc2, pin);

    private static bool Has(ulong mask, uint pin) => pin < 64 && ((mask >> (int)pin) & 1) != 0;
}
