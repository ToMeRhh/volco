using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Volco.Core.Communication;

namespace Volco.App.Controls;

/// <summary>The controller's icon PNGs (linked from firmware/assets/icons) and screen font.</summary>
internal static class ScreenIcons
{
    private static readonly Dictionary<ScreenIcon, Bitmap> Cache = new();

    /// <summary>Arimo, the face the firmware's fonts are generated from.</summary>
    public static FontFamily Font { get; } = new("avares://Volco.App/Assets/Fonts#Arimo");

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
}
