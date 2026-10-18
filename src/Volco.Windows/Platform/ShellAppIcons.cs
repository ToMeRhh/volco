using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Volco.App.Platform;
using Volco.Core.Diagnostics;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Volco.Windows.Platform;

/// <summary>
/// <see cref="IAppIcons"/> through the shell: the thumbnail of the app's file, which for an exe is
/// its icon. Cached by (source, size); a failure is a null (and cached as one).
/// </summary>
internal sealed class ShellAppIcons : IAppIcons
{
    private readonly ConcurrentDictionary<(string Source, int Size), Task<Bitmap?>> _cache = new();

    public Task<Bitmap?> GetIconAsync(string iconSource, int size)
        => _cache.GetOrAdd((iconSource, size), key => LoadAsync(key.Source, key.Size));

    private static async Task<Bitmap?> LoadAsync(string source, int size)
    {
        try
        {
            // "path,index" is how a resource icon is named; the thumbnail is of the file.
            var comma = source.LastIndexOf(',');
            var path = comma > 0 && int.TryParse(source.AsSpan(comma + 1), out _) ? source[..comma] : source;
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, (uint)size);
            if (thumbnail is null || thumbnail.Size == 0) return null;
            using var stream = thumbnail.AsStream();
            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            AppLog.Debug($"App icon: could not load {source} ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }
}
