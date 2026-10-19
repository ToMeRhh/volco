using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Diagnostics;

namespace Volco.App.Platform;

/// <summary>
/// An app's icon by its exe name ("spotify.exe"), for the slider tiles, the inspector and the volume
/// pop-up, through <see cref="IAppIcons"/>: the icon of the app's first live session that has one,
/// else of its exe at the path its slider saved (<see cref="SliderConfig.AppPaths"/>). A live
/// session's exe path is saved there, so the icon also shows after the app quits, in later runs
/// too. Once loaded an icon is kept for the rest of the run. One instance for the whole app; UI
/// thread only.
/// </summary>
public sealed class AppIconLookup : IDisposable
{
    private readonly IAudioEngine _audio;
    private readonly IAppIcons _icons;
    private readonly ConfigManager? _config;
    private readonly Dictionary<string, Bitmap> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <param name="config">Where the exe paths are saved; null: only live sessions.</param>
    public AppIconLookup(IAudioEngine audio, IAppIcons icons, ConfigManager? config = null)
    {
        _audio = audio;
        _icons = icons;
        _config = config;
        _audio.SessionsChanged += OnSessionsChanged;
    }

    /// <summary>On the UI thread: an icon arrived, or an app started (so <see cref="Get"/> may now
    /// find one). Callers ask again.</summary>
    public event Action? Changed;

    /// <summary>The first of <paramref name="apps"/> that has an icon, or null. Apps whose icon is not
    /// loaded yet are asked for; <see cref="Changed"/> follows when one arrives.</summary>
    public Bitmap? Get(IEnumerable<string> apps)
    {
        Bitmap? found = null;
        foreach (var app in apps)
        {
            if (_loaded.TryGetValue(app, out var icon)) { found ??= icon; continue; }
            Request(app);
        }
        return found;
    }

    private void Request(string app)
    {
        if (_disposed || _loading.Contains(app)) return;
        var sessions = _audio.GetSessions()
            .Where(s => !s.IsSystemSounds && string.Equals(s.ImageName, app, StringComparison.OrdinalIgnoreCase)).ToList();
        if (sessions.Select(s => s.ExePath).FirstOrDefault(p => p.Length > 0) is { } exe
            && _config is not null && _config.Config.RememberAppPath(app, exe))
            _config.RequestSave();
        var source = sessions.Select(s => s.IconPath).FirstOrDefault(p => !string.IsNullOrEmpty(p))
                     ?? _config?.Config.AppPathFor(app);
        if (source is null) return;
        _loading.Add(app);
        _ = LoadAsync(app, source);
    }

    private async Task LoadAsync(string app, string source)
    {
        // Asked at 32 px for the 16-20 px slots, so it stays sharp at 200 % scale.
        Bitmap? icon = null;
        try { icon = await _icons.GetIconAsync(source, 32); }
        catch (Exception ex) { AppLog.Debug($"App icon: {source} failed ({ex.GetType().Name}: {ex.Message})"); }
        Dispatcher.UIThread.Post(() =>
        {
            // A failure stays in _loading: not asked again until the app's sessions change.
            if (_disposed || icon is null) return;
            _loading.Remove(app);
            _loaded[app] = icon;
            Changed?.Invoke();
        });
    }

    /// <summary>Any thread. A new or changed session may bring an icon (or a better source after a
    /// failed one), so the failed apps are asked for again.</summary>
    private void OnSessionsChanged(object? sender, SessionsChangedEventArgs e)
    {
        if (e.Kind == SessionChangeKind.Expired) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || _loaded.ContainsKey(e.Session.ImageName)) return;
            _loading.Remove(e.Session.ImageName);
            Changed?.Invoke();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _audio.SessionsChanged -= OnSessionsChanged;
    }
}
