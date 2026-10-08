using Volco.Core.Config;
using Volco.Core.Media;
using Volco.Core.Programs;
using Volco.Core.Scripts;

namespace Volco.Core.Runtime;

/// <summary>A media action's target as the display sees it: whether it has a session, its
/// playback, and its name ("" when Current has no session).</summary>
public sealed record MediaStatus(bool Present, MediaPlayback Playback, string Name);

/// <summary>
/// The live state of media, program and script actions, for
/// <see cref="Communication.ControllerDisplay"/> and the settings previews (custom-actions spec §2.2).
/// Read-only queries; safe from any thread.
/// </summary>
public interface IActionStatus
{
    /// <summary><paramref name="target"/> null: the current app.</summary>
    MediaStatus Media(MediaAppConfig? target);

    ProgramState Program(CommandConfig? command);

    ScriptStatus Script(ButtonActionConfig action);
}

/// <summary><see cref="IActionStatus"/> over the services this platform has; a missing one reads
/// as nothing playing, not running, idle.</summary>
public sealed class ActionStatus : IActionStatus
{
    private readonly IMediaSessions? _media;
    private readonly ProgramOpener? _programs;
    private readonly ScriptRunner? _scripts;

    /// <summary>No services: everything idle.</summary>
    public static IActionStatus None { get; } = new ActionStatus(null, null, null);

    public ActionStatus(IMediaSessions? media, ProgramOpener? programs, ScriptRunner? scripts)
    {
        _media = media;
        _programs = programs;
        _scripts = scripts;
    }

    public MediaStatus Media(MediaAppConfig? target)
    {
        var configured = target?.Name ?? "";
        var id = target is { Id.Length: > 0 } ? target.Id : _media?.CurrentAppId;
        var app = id is null ? null
            : _media?.GetApps().FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        if (app is null) return new MediaStatus(false, MediaPlayback.Stopped, configured);
        return new MediaStatus(true, app.Playback, configured.Length > 0 ? configured : app.Name);
    }

    public ProgramState Program(CommandConfig? command) => _programs?.State(command) ?? ProgramState.Untracked;

    public ScriptStatus Script(ButtonActionConfig action) => _scripts?.Status(action) ?? ScriptStatus.Idle;
}
