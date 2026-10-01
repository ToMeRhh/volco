using Avalonia.Platform;
using Avalonia.Styling;

namespace Volco.App;

/// <summary>
/// The app's theme variants. Light and Dark follow the OS; when the OS asks for high contrast,
/// the app switches to one of the two high-contrast variants below (Colors.axaml), picked by
/// whether the OS high-contrast theme is light or dark. Each inherits its base variant, so Fluent's
/// control resources and every Light/Dark dictionary still apply; only the colour tokens change.
/// </summary>
public static class VolcoThemes
{
    public static ThemeVariant HighContrastDark { get; } = new("HighContrastDark", ThemeVariant.Dark);
    public static ThemeVariant HighContrastLight { get; } = new("HighContrastLight", ThemeVariant.Light);

    /// <summary>The variant to request for the OS's current colour settings.</summary>
    public static ThemeVariant Select(PlatformColorValues? values)
    {
        if (values is not { ContrastPreference: ColorContrastPreference.High }) return ThemeVariant.Default;
        return values.ThemeVariant == PlatformThemeVariant.Light ? HighContrastLight : HighContrastDark;
    }
}
