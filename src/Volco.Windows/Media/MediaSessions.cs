using Windows.Foundation;
using Windows.Media.Control;
using Volco.Core.Diagnostics;
using Volco.Core.Media;
using WinAppInfo = Windows.ApplicationModel.AppInfo;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace Volco.Windows.Media;

/// <summary>
/// <see cref="IMediaSessions"/> over Windows' media sessions (custom-actions spec §3): the ones the
/// volume flyout's media controls show. The manager is requested in the background at start;
/// until then (or if it fails, which is logged once) there are no apps and commands are no-ops.
/// A snapshot (per app id: name and playback) is rebuilt on every manager and session event and
/// read under a lock, so the controller display's queries never call WinRT.
/// </summary>
internal sealed class MediaSessions : IMediaSessions, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private SessionManager? _manager;
    private List<Session> _subscribed = new();
    private IReadOnlyList<MediaApp> _apps = Array.Empty<MediaApp>();
    private string? _current;
    private int _disposed;

    public MediaSessions()
    {
        _ = StartAsync();
    }

    public event EventHandler? Changed;

    public string? CurrentAppId { get { lock (_gate) return _current; } }

    public IReadOnlyList<MediaApp> GetApps() { lock (_gate) return _apps; }

    private async Task StartAsync()
    {
        try
        {
            var manager = await SessionManager.RequestAsync();
            if (Volatile.Read(ref _disposed) != 0) return;
            manager.SessionsChanged += OnSessionsChanged;
            manager.CurrentSessionChanged += OnCurrentSessionChanged;
            lock (_gate) _manager = manager;
            Refresh();
            AppLog.Info("Media sessions: ready");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Media sessions are not available ({ex.GetType().Name}: {ex.Message}); media buttons will do nothing");
        }
    }

    private void OnSessionsChanged(SessionManager sender, SessionsChangedEventArgs args) => Refresh();
    private void OnCurrentSessionChanged(SessionManager sender, CurrentSessionChangedEventArgs args) => Refresh();
    private void OnPlaybackInfoChanged(Session sender, PlaybackInfoChangedEventArgs args) => Refresh();

    /// <summary>Re-reads every session (and re-subscribes to each one's playback changes).</summary>
    private void Refresh()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        SessionManager? manager;
        lock (_gate) manager = _manager;
        if (manager is null) return;

        List<Session> sessions;
        string? current;
        try
        {
            sessions = manager.GetSessions().ToList();
            current = manager.GetCurrentSession()?.SourceAppUserModelId;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Media sessions: could not read them ({ex.GetType().Name}: {ex.Message})");
            return;
        }

        var apps = new Dictionary<string, MediaApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in sessions)
        {
            string id;
            MediaPlayback playback;
            try
            {
                id = session.SourceAppUserModelId;
                playback = Map(session.GetPlaybackInfo()?.PlaybackStatus);
            }
            catch (Exception)
            {
                continue;   // a session that closed while being read
            }
            if (string.IsNullOrEmpty(id)) continue;
            // Several sessions of one app: it counts as playing when any of them plays.
            if (apps.TryGetValue(id, out var seen) && seen.Playback >= playback) continue;
            apps[id] = new MediaApp(id, NameOf(id), playback);
        }

        List<Session> old;
        lock (_gate)
        {
            old = _subscribed;
            _subscribed = sessions;
            _apps = apps.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            _current = current;
        }
        foreach (var session in old) TryUnsubscribe(session);
        foreach (var session in sessions)
        {
            try { session.PlaybackInfoChanged += OnPlaybackInfoChanged; }
            catch (Exception) { /* closed meanwhile */ }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void TryUnsubscribe(Session session)
    {
        try { session.PlaybackInfoChanged -= OnPlaybackInfoChanged; }
        catch (Exception) { /* already gone */ }
    }

    private static MediaPlayback Map(PlaybackStatus? status) => status switch
    {
        PlaybackStatus.Playing => MediaPlayback.Playing,
        PlaybackStatus.Paused or PlaybackStatus.Changing => MediaPlayback.Paused,
        _ => MediaPlayback.Stopped,
    };

    /// <summary>A packaged app's display name; else the id without ".exe" ("Spotify.exe" → "Spotify").</summary>
    private string NameOf(string id)
    {
        lock (_gate)
            if (_names.TryGetValue(id, out var cached)) return cached;
        string name;
        try { name = WinAppInfo.GetFromAppUserModelId(id).DisplayInfo.DisplayName; }
        catch (Exception) { name = ""; }
        if (string.IsNullOrWhiteSpace(name))
            name = id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? id[..^4] : id;
        lock (_gate) _names[id] = name;
        return name;
    }

    public Task<bool> Send(string? appId, MediaCommand command)
        => Task.Run(async () =>
        {
            try { return await SendAsync(appId, command); }
            catch (Exception ex)
            {
                AppLog.Error($"Media {command}: failed ({ex.GetType().Name}: {ex.Message})");
                return false;
            }
        });

    private async Task<bool> SendAsync(string? appId, MediaCommand command)
    {
        SessionManager? manager;
        lock (_gate) manager = _manager;
        if (manager is null)
        {
            AppLog.Info($"Media {command}: no media sessions");
            return false;
        }
        var session = appId is null
            ? manager.GetCurrentSession()
            : manager.GetSessions()
                     .Where(s => string.Equals(s.SourceAppUserModelId, appId, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(s => Map(s.GetPlaybackInfo()?.PlaybackStatus))
                     .FirstOrDefault();
        if (session is null)
        {
            AppLog.Info($"Media {command}: {appId ?? "no current app"} has no session");
            return false;
        }
        var controls = session.GetPlaybackInfo()?.Controls;
        IAsyncOperation<bool>? operation = command switch
        {
            MediaCommand.PlayPause when controls?.IsPlayPauseToggleEnabled != false => session.TryTogglePlayPauseAsync(),
            MediaCommand.Next when controls?.IsNextEnabled != false => session.TrySkipNextAsync(),
            MediaCommand.Previous when controls?.IsPreviousEnabled != false => session.TrySkipPreviousAsync(),
            MediaCommand.Stop when controls?.IsStopEnabled != false => session.TryStopAsync(),
            _ => null,
        };
        if (operation is null)
        {
            AppLog.Info($"Media {command}: {session.SourceAppUserModelId} has that control disabled");
            return false;
        }
        if (await operation) return true;
        AppLog.Info($"Media {command}: {session.SourceAppUserModelId} did not accept it");
        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        SessionManager? manager;
        List<Session> sessions;
        lock (_gate)
        {
            manager = _manager;
            _manager = null;
            sessions = _subscribed;
            _subscribed = new();
        }
        foreach (var session in sessions) TryUnsubscribe(session);
        if (manager is null) return;
        try
        {
            manager.SessionsChanged -= OnSessionsChanged;
            manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        }
        catch (Exception) { /* shutting down */ }
    }
}
