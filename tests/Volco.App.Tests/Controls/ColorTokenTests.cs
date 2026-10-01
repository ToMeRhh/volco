using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;

namespace Volco.App.Tests.Controls;

/// <summary>
/// Checks the colour tokens against the contrast targets in the UX spec (§3.7), computed from the
/// actual resources rather than the spec's table: text tokens at least 4.5:1 (WCAG AA) on both
/// Surface and Bg, and the accent and input outlines at least 3:1 (non-text UI), in Light and Dark.
/// </summary>
public class ColorTokenTests
{
    private static Color Token(string key, ThemeVariant theme)
    {
        Assert.True(Application.Current!.TryGetResource(key, theme, out var value), $"{key} missing for {theme}");
        return Assert.IsType<Color>(value);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Contrast(Color a, Color b)
    {
        var (l1, l2) = (Luminance(a), Luminance(b));
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    public static TheoryData<string, string, double> Pairs => new()
    {
        { "TextColor", "SurfaceColor", 4.5 },
        { "TextColor", "BgColor", 4.5 },
        { "TextSecondaryColor", "SurfaceColor", 4.5 },
        { "TextSecondaryColor", "BgColor", 4.5 },
        { "TextTertiaryColor", "SurfaceColor", 4.5 },
        { "TextTertiaryColor", "BgColor", 4.5 },
        { "AccentColor", "SurfaceColor", 4.5 },
        { "OnAccentColor", "AccentColor", 4.5 },
        { "SuccessColor", "SurfaceColor", 4.5 },
        { "WarningColor", "SurfaceColor", 4.5 },
        { "DangerColor", "SurfaceColor", 4.5 },
        { "BorderStrongColor", "SurfaceColor", 3.0 },
    };

    [AvaloniaTheory]
    [MemberData(nameof(Pairs))]
    public void Tokens_MeetTheirContrastTarget_InLightAndDark(string foreground, string background, double minimum)
    {
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var ratio = Contrast(Token(foreground, theme), Token(background, theme));
            Assert.True(ratio >= minimum, $"{foreground} on {background} ({theme}) is {ratio:F2}:1, needs {minimum}:1");
        }
    }

    [AvaloniaFact]
    public void Icons_VolumeAndSpeaker_AreDistinct()
    {
        Assert.True(Application.Current!.TryGetResource("Icon.Volume", null, out var volume));
        Assert.True(Application.Current!.TryGetResource("Icon.Speaker", null, out var speaker));
        // Different drawings, not one glyph under two names.
        Assert.NotEqual(Assert.IsAssignableFrom<Geometry>(volume).Bounds, Assert.IsAssignableFrom<Geometry>(speaker).Bounds);
    }
}
