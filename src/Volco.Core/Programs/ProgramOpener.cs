using Volco.Core.Config;
using Volco.Core.Diagnostics;

namespace Volco.Core.Programs;

/// <summary>Whether an Open a program button's program runs.</summary>
public enum ProgramState
{
    /// <summary>A URL, a document or a shortcut without a local exe: nothing to match.</summary>
    Untracked,
    NotRunning,
    Running
}

/// <summary>
/// What an Open a program press does (custom-actions spec §4): bring the program to the front
/// when it runs, else start it, but not twice within <see cref="LaunchDebounce"/>. Also resolves
/// which executable a command stands for (an exe, or a shortcut's exe target), for running
/// detection. Thread-safe.
/// </summary>
public sealed class ProgramOpener
{
    private readonly IProgramLauncher _launcher;
    private readonly TimeProvider _time;
    private readonly Func<string, string?> _resolveLnk;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _lastLaunch = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _lnkTargets = new(StringComparer.OrdinalIgnoreCase);
    private List<CommandConfig> _watched = new();

    /// <summary>How long after a launch further presses are ignored, so a double tap does not
    /// start two copies while the first is still loading.</summary>
    public static TimeSpan LaunchDebounce { get; } = TimeSpan.FromSeconds(5);

    /// <param name="resolveLnk">A shortcut's target; null: <see cref="LnkFile.TryReadTarget(string)"/>.</param>
    public ProgramOpener(IProgramLauncher launcher, TimeProvider? time = null, Func<string, string?>? resolveLnk = null)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _time = time ?? TimeProvider.System;
        _resolveLnk = resolveLnk ?? LnkFile.TryReadTarget;
        _launcher.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A watched program started or exited. Any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether <paramref name="command"/> names an exe or a shortcut, whose running
    /// state can be known (without reading the shortcut).</summary>
    public static bool IsTrackable(CommandConfig? command)
    {
        var path = CommandPath.Unquote(command?.Path);
        return path.Length > 0 && !CommandPath.IsUrl(path) && CommandPath.Extension(path) is ".exe" or ".lnk";
    }

    /// <summary>The executable <paramref name="command"/> runs as: an exe path itself, a
    /// shortcut's target when that is an exe, else null (URLs, documents).</summary>
    public string? TrackedPath(CommandConfig? command)
    {
        var path = CommandPath.Unquote(command?.Path);
        if (path.Length == 0 || CommandPath.IsUrl(path)) return null;
        var ext = CommandPath.Extension(path);
        if (ext == ".exe") return path;
        if (ext != ".lnk") return null;
        string? target;
        lock (_gate)
        {
            if (!_lnkTargets.TryGetValue(path, out target))
                _lnkTargets[path] = target = _resolveLnk(path);
        }
        return target is not null && CommandPath.Extension(target) == ".exe" ? target : null;
    }

    public ProgramState State(CommandConfig? command)
        => TrackedPath(command) is { } exe
            ? _launcher.IsRunning(exe) ? ProgramState.Running : ProgramState.NotRunning
            : ProgramState.Untracked;

    /// <summary>Brings the program to the front, or starts it. May block while the shell starts
    /// it (a UAC prompt): callers run it off the input thread. False when nothing is chosen or it
    /// could not start (a press ignored as a double tap, or a running program without a window,
    /// is not a failure).</summary>
    public bool Open(CommandConfig command)
    {
        command = CommandPath.Unquoted(command);
        var path = command.Path;
        if (path.Length == 0)
        {
            AppLog.Info("Open a program: no program chosen");
            return false;
        }
        RereadShortcut(path);
        var exe = TrackedPath(command);
        if (exe is not null && _launcher.IsRunning(exe))
        {
            if (!_launcher.BringToFront(exe)) AppLog.Info($"Open a program: {exe} runs but has no window to bring to the front");
            return true;
        }
        if (exe is not null)
        {
            var now = _time.GetTimestamp();
            lock (_gate)
            {
                if (_lastLaunch.TryGetValue(exe, out var last) && _time.GetElapsedTime(last, now) < LaunchDebounce)
                {
                    AppLog.Info($"Open a program: {exe} was just started; press ignored");
                    return true;
                }
                _lastLaunch[exe] = now;
            }
        }
        AppLog.Info($"Open a program: starting {path}");
        if (_launcher.Start(command)) return true;
        // A failed start must not block the next press.
        if (exe is not null) lock (_gate) _lastLaunch.Remove(exe);
        return false;
    }

    /// <summary>Polls the executables of <paramref name="commands"/> (the live mode's programs).
    /// Shortcut targets are read again, in case a shortcut changed.</summary>
    public void Watch(IEnumerable<CommandConfig> commands)
    {
        var list = commands.ToList();
        lock (_gate)
        {
            _lnkTargets.Clear();
            _watched = list;
        }
        _launcher.Watch(TrackedPaths(list));
    }

    private IReadOnlyCollection<string> TrackedPaths(IEnumerable<CommandConfig> commands)
        => commands.Select(TrackedPath).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>A press (or Try) uses the shortcut at <paramref name="path"/>: its target is read
    /// again, since an update may have pointed it at a new exe (a new version's folder). When the
    /// target changed, the running poll follows it, so the light does too. Shortcuts are otherwise
    /// read only by <see cref="Watch"/> (a mode change, a config change, the start).</summary>
    private void RereadShortcut(string path)
    {
        if (CommandPath.IsUrl(path) || CommandPath.Extension(path) != ".lnk") return;
        var target = _resolveLnk(path);
        List<CommandConfig> watched;
        lock (_gate)
        {
            var known = _lnkTargets.TryGetValue(path, out var old);
            _lnkTargets[path] = target;
            if (!known || string.Equals(old, target, StringComparison.OrdinalIgnoreCase)) return;
            AppLog.Info($"Open a program: {path} now points at {target ?? "nothing"}");
            watched = _watched;
        }
        _launcher.Watch(TrackedPaths(watched));
        Changed?.Invoke(this, EventArgs.Empty);   // the light reads the new target
    }
}
