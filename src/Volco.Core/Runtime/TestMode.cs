namespace Volco.Core.Runtime;

/// <summary>What an LED button shows during a hardware test (spec §8.5): a single LED is lit by
/// On (or A or B); an LED pair lights A or B.</summary>
public enum TestIndicator { Off, On, A, B }

public enum RawInputKind { Slider, Button }

/// <summary>A Slider or Button message as the controller sent it, raised only in test mode.</summary>
/// <param name="Value">A slider's raw value (0–4095), or 1 pressed / 0 released for a button.</param>
public sealed class RawInputEventArgs(RawInputKind kind, int index, int value) : EventArgs
{
    public RawInputKind Kind { get; } = kind;
    public int Index { get; } = index;
    public int Value { get; } = value;
}
