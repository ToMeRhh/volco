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
        var path = command?.Path.Trim() ?? "";
        return path.Length > 0 && !CommandPath.IsUrl(path) && CommandPath.Extension(path) is ".exe" or ".lnk";
    }

    /// <summary>The executable <paramref name="command"/> runs as: an exe path itself, a
    /// shortcut's target when that is an exe, else null (URLs, documents).</summary>
    public string? TrackedPath(CommandConfig? command)
    {
        var path = command?.Path.Trim() ?? "";
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
    /// it (a UAC prompt): callers run it off the input thread.</summary>
    public void Open(CommandConfig command)
    {
        var path = command.Path.Trim();
        if (path.Length == 0)
        {
            AppLog.Info("Open a program: no program chosen");
            return;
        }
        var exe = TrackedPath(command);
        if (exe is not null && _launcher.IsRunning(exe))
        {
            if (!_launcher.BringToFront(exe)) AppLog.Info($"Open a program: {exe} runs but has no window to bring to the front");
            return;
        }
        if (exe is not null)
        {
            var now = _time.GetTimestamp();
            lock (_gate)
            {
                if (_lastLaunch.TryGetValue(exe, out var last) && _time.GetElapsedTime(last, now) < LaunchDebounce)
                {
                    AppLog.Info($"Open a program: {exe} was just started; press ignored");
                    return;
                }
                _lastLaunch[exe] = now;
            }
        }
        AppLog.Info($"Open a program: starting {path}");
        if (!_launcher.Start(command))
        {
            // A failed start must not block the next press.
            if (exe is not null) lock (_gate) _lastLaunch.Remove(exe);
        }
    }

    /// <summary>Polls the executables of <paramref name="commands"/> (the live preset's programs).
    /// Shortcut targets are read again, in case a shortcut changed.</summary>
    public void Watch(IEnumerable<CommandConfig> commands)
    {
        lock (_gate) _lnkTargets.Clear();
        var paths = commands.Select(TrackedPath).OfType<string>()
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _launcher.Watch(paths);
    }
}
