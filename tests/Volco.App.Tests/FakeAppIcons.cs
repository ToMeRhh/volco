using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Volco.App.Platform;

namespace Volco.App.Tests;

/// <summary>An <see cref="IAppIcons"/> that returns a 1 × 1 bitmap for every source (one bitmap per
/// source) and records what was asked.</summary>
internal sealed class FakeAppIcons : IAppIcons
{
    private readonly Dictionary<string, Bitmap> _bitmaps = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Requests { get; } = new();

    public Task<Bitmap?> GetIconAsync(string iconSource, int size)
    {
        Requests.Add(iconSource);
        if (!_bitmaps.TryGetValue(iconSource, out var bitmap))
            _bitmaps[iconSource] = bitmap = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        return Task.FromResult<Bitmap?>(bitmap);
    }

    /// <summary>The bitmap returned for <paramref name="iconSource"/>, once asked.</summary>
    public Bitmap Of(string iconSource) => _bitmaps[iconSource];
}
