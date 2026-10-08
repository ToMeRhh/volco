using Volco.Core.Communication;

namespace Volco.Core.Runtime;

/// <summary>What an LED button shows during a hardware test (spec §8.5): a single LED is lit by
/// On (or A or B); an LED pair lights A or B.</summary>
public enum TestIndicator { Off, On, A, B }

public enum RawInputKind { Slider, Button }

/// <summary>A Slider or Button message as the controller sent it, raised only in test mode.</summary>
/// <param name="Value">A slider's raw value (0–4095), or 1 pressed / 0 released for a button.</param>
/// <param name="Presses">A button's presses so far in this test (0 for a slider).</param>
public sealed class RawInputEventArgs(RawInputKind kind, int index, int value, int presses = 0) : EventArgs
{
    public RawInputKind Kind { get; } = kind;
    public int Index { get; } = index;
    public int Value { get; } = value;
    public int Presses { get; } = presses;
}

/// <summary>What a button shows during the test after <c>presses</c> presses (spec §8.5): each
/// press toggles a single LED, moves an LED pair to its other side (Off, then A, B, A, …), and
/// counts on a screen.</summary>
public static class TestMode
{
    public static TestIndicator Indicator(IndicatorKind kind, int presses) => (kind, presses) switch
    {
        (_, <= 0) => TestIndicator.Off,
        (IndicatorKind.Led, _) => presses % 2 == 1 ? TestIndicator.On : TestIndicator.Off,
        (IndicatorKind.LedPair, _) => presses % 2 == 1 ? TestIndicator.A : TestIndicator.B,
        _ => TestIndicator.Off,
    };

    /// <summary>"1 press", "3 presses".</summary>
    public static string PressText(int presses) => presses == 1 ? "1 press" : $"{presses} presses";

    /// <summary>"Test" over "Button N" until the first press, then "Button N" over the count.</summary>
    public static ScreenContent Screen(int button, int presses) => presses <= 0
        ? ScreenContent.Text("Test", ScreenIcon.Generic, false, $"Button {button}")
        : ScreenContent.Text($"Button {button}", ScreenIcon.Generic, false, PressText(presses));
}
