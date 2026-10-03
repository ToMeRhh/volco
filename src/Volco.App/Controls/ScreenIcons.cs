using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Volco.Core.Communication;

namespace Volco.App.Controls;

/// <summary>The controller's icon PNGs (linked from firmware/assets/icons) and screen font, plus the
/// vector versions of the same icons for the rest of the desktop UI.</summary>
internal static class ScreenIcons
{
    private static readonly Dictionary<ScreenIcon, Bitmap> Cache = new();

    /// <summary>Arimo, the face the firmware's fonts are generated from.</summary>
    public static FontFamily Font { get; } = new("avares://Volco.App/Assets/Fonts#Arimo");

    /// <summary>The 1-bit OLED art, for the screen preview only: it is crisp at 1:1 pixels and
    /// pixelated at any other size.</summary>
    public static Bitmap Bitmap(ScreenIcon icon)
    {
        // A hand-edited config can hold any number; there is no PNG for an undefined one.
        if (!Enum.IsDefined(icon)) icon = ScreenIcon.Generic;
        lock (Cache)
        {
            if (!Cache.TryGetValue(icon, out var bitmap))
            {
                // File stems are the lower-case enum names (manifest.txt: StudioMic -> studiomic).
                var uri = new Uri($"avares://Volco.App/Assets/Icons/{icon.ToString().ToLowerInvariant()}.png");
                Cache[icon] = bitmap = new Bitmap(AssetLoader.Open(uri));
            }
            return bitmap;
        }
    }

    /// <summary>The icon's vector art (Icon.Device.* in Styles/Icons.axaml, Icon.Preset for
    /// <see cref="ScreenIcon.Presets"/>), for the OSD and the Devices page. Null when the app's
    /// resources are not loaded.</summary>
    public static Geometry? Geometry(ScreenIcon icon)
    {
        if (!Enum.IsDefined(icon)) icon = ScreenIcon.Generic;
        var key = icon == ScreenIcon.Presets ? "Icon.Preset" : $"Icon.Device.{icon}";
        return Application.Current is { } app && app.TryFindResource(key, out var value) ? value as Geometry : null;
    }
}
