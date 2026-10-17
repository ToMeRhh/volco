using Volco.Core.Controller;
using Xunit;

namespace Volco.Core.Tests.Controller;

/// <summary>The generated ESP32 table equals spec §6.4's (and the hand-written one it replaced).</summary>
public class ChipInfoTableTests
{
    private static readonly HashSet<uint> Exists = [.. Range(0, 19), 21, 22, 23, 25, 26, 27, .. Range(32, 39)];
    private static readonly HashSet<uint> Reserved = [1, 3, .. Range(6, 11)];
    private static readonly HashSet<uint> InputOnly = [.. Range(34, 39)];
    private static readonly HashSet<uint> Adc = [.. Range(32, 39), 0, 2, 4, .. Range(12, 15), 25, 26, 27];
    private static readonly HashSet<uint> Adc2 = [0, 2, 4, .. Range(12, 15), 25, 26, 27];

    private static IEnumerable<uint> Range(uint a, uint b)
    {
        for (var p = a; p <= b; p++) yield return p;
    }

    [Fact]
    public void Esp32_matches_the_spec_for_every_pin()
    {
        var c = ChipInfo.Esp32;
        for (uint p = 0; p < 70; p++)
        {
            bool usable = Exists.Contains(p) && !Reserved.Contains(p);
            Assert.Equal(Exists.Contains(p), c.Exists(p));
            Assert.Equal(usable, c.Usable(p));
            Assert.Equal(usable && !InputOnly.Contains(p), c.OutputCapable(p));
            Assert.Equal(usable && Adc.Contains(p), c.AdcCapable(p));
            Assert.Equal(Adc2.Contains(p), c.IsAdc2(p));
        }
    }
}
