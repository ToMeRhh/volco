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
    private static readonly Dictionary<(ScreenIcon, int), Bitmap> Cache = new();

    /// <summary>Arimo, the face the firmware's fonts are generated from.</summary>
    public static FontFamily Font { get; } = new("avares://Volco.App/Assets/Fonts#Arimo");

    /// <summary>The 1-bit OLED art, for the screen preview only: it is crisp at 1:1 pixels and
    /// pixelated at any other size. <paramref name="frame"/> picks an animated icon's frame.</summary>
    public static Bitmap Bitmap(ScreenIcon icon, int frame = 0)
    {
        // A hand-edited config can hold any number; there is no PNG for an undefined one.
        if (!Enum.IsDefined(icon)) icon = ScreenIcon.Generic;
        var frames = ScreenIconSets.FrameCount(icon);
        frame = ((frame % frames) + frames) % frames;
        lock (Cache)
        {
            if (!Cache.TryGetValue((icon, frame), out var bitmap))
            {
                // File stems are the lower-case enum names (manifest.txt: StudioMic -> studiomic);
                // an animated icon's frames are <stem>_0.png, <stem>_1.png, ...
                var stem = icon.ToString().ToLowerInvariant();
                var file = frames > 1 ? $"{stem}_{frame}" : stem;
                var uri = new Uri($"avares://Volco.App/Assets/Icons/{file}.png");
                Cache[(icon, frame)] = bitmap = new Bitmap(AssetLoader.Open(uri));
            }
            return bitmap;
        }
    }

    /// <summary>The icon's vector art (Icon.Device.* in Styles/Icons.axaml, Icon.Mode for
    /// <see cref="ScreenIcon.Presets"/>), for the OSD and the device dialog. Null when the app's
    /// resources are not loaded.</summary>
    public static Geometry? Geometry(ScreenIcon icon)
    {
        if (!Enum.IsDefined(icon)) icon = ScreenIcon.Generic;
        var key = icon == ScreenIcon.Presets ? "Icon.Mode" : $"Icon.Device.{icon}";
        return Application.Current is { } app && app.TryFindResource(key, out var value) ? value as Geometry : null;
    }
}
