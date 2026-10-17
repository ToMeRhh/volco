using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Volco.App.Controls;
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

    /// <summary>High contrast: text, accent and status at least 7:1 (WCAG AAA), borders 3:1.</summary>
    public static TheoryData<string, string, double> HighContrastPairs => new()
    {
        { "TextColor", "SurfaceColor", 7.0 },
        { "TextColor", "SubtleColor", 7.0 },
        { "TextSecondaryColor", "SurfaceColor", 7.0 },
        { "TextSecondaryColor", "SubtleColor", 7.0 },
        { "TextTertiaryColor", "SurfaceColor", 7.0 },
        { "TextTertiaryColor", "SubtleColor", 7.0 },
        { "TextColor", "AccentSoftColor", 7.0 },
        { "AccentColor", "SurfaceColor", 7.0 },
        { "OnAccentColor", "AccentColor", 7.0 },
        { "SuccessColor", "SurfaceColor", 7.0 },
        { "WarningColor", "SurfaceColor", 7.0 },
        { "DangerColor", "SurfaceColor", 7.0 },
        { "BorderColor", "SurfaceColor", 3.0 },
        { "BorderStrongColor", "SurfaceColor", 3.0 },
    };

    [AvaloniaTheory]
    [MemberData(nameof(HighContrastPairs))]
    public void Tokens_MeetTheirContrastTarget_InHighContrast(string foreground, string background, double minimum)
    {
        foreach (var theme in new[] { VolcoThemes.HighContrastDark, VolcoThemes.HighContrastLight })
        {
            var ratio = Contrast(Token(foreground, theme), Token(background, theme));
            Assert.True(ratio >= minimum, $"{foreground} on {background} ({theme}) is {ratio:F2}:1, needs {minimum}:1");
        }
    }

    /// <summary>The preset tints (Buttons page): every text token reads on each fill, at 4.5:1
    /// (7:1 in high contrast), and each edge stands out from the page and from its fill at 3:1.</summary>
    [AvaloniaFact]
    public void PresetTints_MeetTheirContrastTargets_InEveryTheme()
    {
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark, VolcoThemes.HighContrastDark, VolcoThemes.HighContrastLight })
        {
            var text = theme == ThemeVariant.Light || theme == ThemeVariant.Dark ? 4.5 : 7.0;
            for (int i = 0; i < PresetTint.Count; i++)
            {
                var fill = Token($"PresetFill{i}Color", theme);
                var edge = Token($"PresetEdge{i}Color", theme);
                foreach (var key in new[] { "TextColor", "TextSecondaryColor", "TextTertiaryColor" })
                {
                    var ratio = Contrast(Token(key, theme), fill);
                    Assert.True(ratio >= text, $"{key} on PresetFill{i} ({theme}) is {ratio:F2}:1, needs {text}:1");
                }
                Assert.True(Contrast(edge, Token("BgColor", theme)) >= 3.0, $"PresetEdge{i} on Bg ({theme})");
                Assert.True(Contrast(edge, fill) >= 3.0, $"PresetEdge{i} on PresetFill{i} ({theme})");
            }
        }
    }

    [AvaloniaFact]
    public void HighContrast_BrushesPickUpTheHighContrastColours()
    {
        // The brushes live in the Light/Dark dictionaries and reference the colours dynamically;
        // the high-contrast variants inherit those dictionaries, so the brushes must resolve to
        // the high-contrast colours, not the base theme's.
        var border = new Border();
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("SurfaceBrush"));
        var window = new Window { Content = border, RequestedThemeVariant = VolcoThemes.HighContrastDark };
        window.Show();
        try
        {
            Assert.Equal(Token("SurfaceColor", VolcoThemes.HighContrastDark),
                         Assert.IsAssignableFrom<ISolidColorBrush>(border.Background).Color);
            Assert.NotEqual(Token("SurfaceColor", ThemeVariant.Dark),
                            Assert.IsAssignableFrom<ISolidColorBrush>(border.Background).Color);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Select_FollowsTheOsContrastPreference()
    {
        Assert.Equal(ThemeVariant.Default, VolcoThemes.Select(null));
        Assert.Equal(ThemeVariant.Default, VolcoThemes.Select(new PlatformColorValues
            { ThemeVariant = PlatformThemeVariant.Dark, ContrastPreference = ColorContrastPreference.NoPreference }));
        Assert.Equal(VolcoThemes.HighContrastDark, VolcoThemes.Select(new PlatformColorValues
            { ThemeVariant = PlatformThemeVariant.Dark, ContrastPreference = ColorContrastPreference.High }));
        Assert.Equal(VolcoThemes.HighContrastLight, VolcoThemes.Select(new PlatformColorValues
            { ThemeVariant = PlatformThemeVariant.Light, ContrastPreference = ColorContrastPreference.High }));
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
