namespace Volco.Core.Media;

/// <summary>What an app's media session is doing.</summary>
public enum MediaPlayback
{
    Stopped,
    Paused,
    Playing
}

/// <summary>A transport command a media button sends.</summary>
public enum MediaCommand
{
    PlayPause,
    Next,
    Previous,
    Stop
}

/// <summary>One app with a media session: its Windows app id, display name and playback state.</summary>
public sealed record MediaApp(string Id, string Name, MediaPlayback Playback);

/// <summary>
/// The system's media sessions (custom-actions spec §3): the seam that keeps
/// <c>Windows.Media.Control</c> out of Core. Windows: <c>Volco.Windows.Media.MediaSessions</c>.
/// </summary>
public interface IMediaSessions
{
    /// <summary>Every app with a session now, one entry per app id.</summary>
    IReadOnlyList<MediaApp> GetApps();

    /// <summary>The app Windows considers current (the volume flyout's), or null for none.</summary>
    string? CurrentAppId { get; }

    /// <summary>Sends <paramref name="command"/> to <paramref name="appId"/> (null: the current
    /// app). Returns at once; never throws. The task says whether the app took it: false (and
    /// logged) for no session, a control the app has disabled, or an app that refused.</summary>
    Task<bool> Send(string? appId, MediaCommand command);

    /// <summary>A session came or went, the current app changed, or a playback state changed.
    /// Any thread.</summary>
    event EventHandler? Changed;
}
