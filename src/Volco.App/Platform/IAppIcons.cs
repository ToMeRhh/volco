using Avalonia.Media.Imaging;

namespace Volco.App.Platform;

/// <summary>
/// The icon of a running app, for the target dialog's app rows. Each OS head implements it with its
/// own shell (Windows: the file's thumbnail); null where there is none, and every app then shows a
/// letter tile.
/// </summary>
public interface IAppIcons
{
    /// <summary>The icon of the file at <paramref name="iconSource"/> (a path, maybe with a
    /// ",index" suffix), about <paramref name="size"/> px square; null on any failure.</summary>
    Task<Bitmap?> GetIconAsync(string iconSource, int size);
}
