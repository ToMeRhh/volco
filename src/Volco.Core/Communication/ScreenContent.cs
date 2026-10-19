using System.Text.Json.Serialization;

namespace Volco.Core.Communication;

/// <summary>How a button shows its state, as the controller reports it in Hello (spec §3.2).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IndicatorKind>))]
public enum IndicatorKind : byte
{
    None = 0,
    Led = 1,
    LedPair = 2,
    Screen = 3,
}

/// <summary>How a screen changes to new content (spec §3.3). Unknown values mean None.</summary>
public enum ScreenTransition : byte
{
    None = 0,
    SlideLeft = 1,
    SlideRight = 2,
}

/// <summary>What layout 1's top band (rows 0–15) holds.</summary>
public enum TopKind : byte
{
    Empty = 0,
    Text = 1,
    Dots = 2,
}

/// <summary>
/// One screen in layout 1 ("C nudged", spec §2.1): top slot, icon (+ muted slash), main text.
/// Text is in normal reading order; <see cref="ProtocolEncoder"/> sanitizes and truncates it.
/// A record, so two contents compare by value (the runtime's dedup relies on it).
/// </summary>
public sealed record ScreenContent(
    TopKind TopKind,
    string TopText,
    byte DotsSelected,
    byte DotsCount,
    ScreenIcon? Icon,
    bool Muted,
    string Main)
{
    /// <summary>The only layout this app sends.</summary>
    public const byte Layout = 1;

    /// <summary>A blank screen ("Not set up").</summary>
    public static ScreenContent Blank { get; } = new(TopKind.Empty, "", 0, 0, null, false, "");

    /// <summary>Top text (empty text means an empty top), icon, main.</summary>
    public static ScreenContent Text(string top, ScreenIcon? icon, bool muted, string main)
        => new(top.Length == 0 ? TopKind.Empty : TopKind.Text, top, 0, 0, icon, muted, main);

    /// <summary>Mode dots on top (selected is 0-based), icon, main.</summary>
    public static ScreenContent Dots(byte selected, byte count, ScreenIcon? icon, string main)
        => new(TopKind.Dots, "", selected, count, icon, false, main);
}
