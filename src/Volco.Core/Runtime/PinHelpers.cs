namespace Volco.Core.Runtime;

/// <summary>One PinDrive of a light try (firmware helpers, Try the lights): at <see cref="At"/> after
/// the try starts, <see cref="Pin"/> is driven <see cref="High"/> or low for <see cref="Duration"/>,
/// then the controller restores it.</summary>
public readonly record struct LightStep(TimeSpan At, int Pin, bool High, TimeSpan Duration);

/// <summary>The flash that Try the lights plays on a light's pins.</summary>
public static class LightTry
{
    private static readonly TimeSpan SingleFlash = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan PairFlash = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan PairGap = TimeSpan.FromMilliseconds(700);

    /// <summary>A single LED (<paramref name="pinB"/> null): lit at 0, unlit at 300 ms, lit at 600 ms,
    /// 300 ms each. A pair: A lit and B unlit at 0, B lit and A unlit at 700 ms, 600 ms each. Lit is high
    /// unless <paramref name="activeLow"/>, unlit the other level: every step drives its level, so a
    /// lit LED still flashes and the other side of a pair stays dark.</summary>
    public static IReadOnlyList<LightStep> Steps(int pinA, int? pinB, bool activeLow)
    {
        bool lit = !activeLow;
        return pinB is { } b
            ? new[]
            {
                new LightStep(TimeSpan.Zero, pinA, lit, PairFlash), new LightStep(TimeSpan.Zero, b, !lit, PairFlash),
                new LightStep(PairGap, b, lit, PairFlash), new LightStep(PairGap, pinA, !lit, PairFlash),
            }
            : new[]
            {
                new LightStep(TimeSpan.Zero, pinA, lit, SingleFlash),
                new LightStep(SingleFlash, pinA, !lit, SingleFlash),
                new LightStep(2 * SingleFlash, pinA, lit, SingleFlash),
            };
    }
}
