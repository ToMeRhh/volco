using System.Runtime.CompilerServices;
using Volco.Core.Config;
using Volco.Core.Diagnostics;
using Volco.Core.Programs;

namespace Volco.Core.Scripts;

/// <summary>A started script run.</summary>
public interface IRunningProcess
{
    /// <summary>Completes with the exit code when the run ends; null when it cannot be known
    /// (a file association that handed the file to an already-running program).</summary>
    Task<int?> Exited { get; }

    /// <summary>The last lines the run wrote to stdout and stderr (empty with the shell).</summary>
    IReadOnlyList<string> OutputTail { get; }
}

/// <summary>Starts processes: the seam that lets tests run scripts without processes.</summary>
public interface IProcessStarter
{
    /// <summary>Starts <paramref name="spec"/>; throws when it cannot start.</summary>
    IRunningProcess Start(ProcessSpec spec);
}

/// <summary>A Run a script action's state for its button: running, and the failure flash's step
/// (0 to <see cref="ScriptRunner.FlashSteps"/> - 1) while it flashes.</summary>
public sealed record ScriptStatus(bool Running, int? FlashStep)
{
    public static ScriptStatus Idle { get; } = new(false, null);
}

/// <summary>
/// Runs scripts for Run a script buttons (custom-actions spec §5): hidden, through
/// <see cref="ScriptInterpreters"/>, at most one run at a time per action unless it says
/// <see cref="WhileRunningPolicy.StartAnother"/>. A failed run (non-zero exit, or one that could
/// not start) is logged with its output's last lines and flashes its button for
/// <see cref="FlashSteps"/> × <see cref="FlashStepInterval"/>. Runs are never killed.
/// State is kept per action object, which is one slot of one button in one mode. Thread-safe;
/// <see cref="Changed"/> is raised on any thread.
/// </summary>
public sealed class ScriptRunner : IDisposable
{
    public const int TailLines = 20;
    // The same rhythm as any other press error (ControllerDisplay.FeedbackStep): a fast flash.
    public const int FlashSteps = 12;
    public static TimeSpan FlashStepInterval { get; } = TimeSpan.FromMilliseconds(125);

    private readonly IProcessStarter _starter;
    private readonly TimeProvider _time;
    private readonly Func<string, string?> _onPath;
    private readonly Func<string, bool> _fileExists;
    private readonly ConditionalWeakTable<ButtonActionConfig, RunState> _states = new();
    private int _disposed;

    private sealed class RunState
    {
        public int Running;
        public int? FlashStep;
        public ITimer? FlashTimer;
    }

    public ScriptRunner(IProcessStarter starter, TimeProvider? time = null, Func<string, string?>? onPath = null,
                        Func<string, bool>? fileExists = null)
    {
        _starter = starter ?? throw new ArgumentNullException(nameof(starter));
        _time = time ?? TimeProvider.System;
        _onPath = onPath ?? PathLookup.Find;
        _fileExists = fileExists ?? File.Exists;
    }

    /// <summary>A run started or ended, or the failure flash moved on.</summary>
    public event EventHandler? Changed;

    public ScriptStatus Status(ButtonActionConfig action)
    {
        if (!_states.TryGetValue(action, out var state)) return ScriptStatus.Idle;
        lock (state) return state.Running == 0 && state.FlashStep is null ? ScriptStatus.Idle : new(state.Running > 0, state.FlashStep);
    }

    /// <summary>Starts a run of <paramref name="action"/>'s script, unless one runs and the
    /// action ignores presses while it does. Returns at once.</summary>
    public void Run(ButtonActionConfig action, IReadOnlyDictionary<string, string> environment)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var command = CommandPath.Unquoted(action.Command ?? new CommandConfig());
        var path = command.Path;
        var name = path.Length == 0 ? "(no script chosen)" : path;
        var state = _states.GetValue(action, _ => new RunState());
        lock (state)
        {
            if (state.Running > 0 && action.WhileRunning == WhileRunningPolicy.Ignore)
            {
                AppLog.Info($"Run a script: {name} is still running; press ignored");
                return;
            }
            state.Running++;
        }

        IRunningProcess process;
        try
        {
            if (path.Length == 0 || !_fileExists(path))
                throw new FileNotFoundException(path.Length == 0 ? "No script is chosen" : $"{path} does not exist");
            var spec = ScriptInterpreters.Resolve(command, _onPath, environment);
            AppLog.Info($"Run a script: {spec.FileName} {spec.Arguments}");
            process = _starter.Start(spec);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Run a script: {name} could not start ({ex.GetType().Name}: {ex.Message})");
            lock (state) state.Running--;
            Fail(state);
            return;
        }
        RaiseChanged();

        process.Exited.ContinueWith(t =>
        {
            lock (state) state.Running--;
            int? code = t.IsCompletedSuccessfully ? t.Result : -1;
            if (code is null or 0)
            {
                AppLog.Info($"Run a script: {name} finished");
                RaiseChanged();
                return;
            }
            var tail = process.OutputTail;
            AppLog.Error($"Run a script: {name} exited with code {code}" +
                         (tail.Count == 0 ? "" : $"; last output:{Environment.NewLine}{string.Join(Environment.NewLine, tail)}"));
            Fail(state);
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    /// <summary>Starts (or restarts) the failure flash.</summary>
    private void Fail(RunState state)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (state)
        {
            state.FlashTimer?.Dispose();
            state.FlashStep = 0;
            state.FlashTimer = _time.CreateTimer(_ => Step(state), null, FlashStepInterval, FlashStepInterval);
        }
        RaiseChanged();
    }

    private void Step(RunState state)
    {
        lock (state)
        {
            if (state.FlashStep is not { } step) return;
            if (step + 1 < FlashSteps) state.FlashStep = step + 1;
            else
            {
                state.FlashStep = null;
                state.FlashTimer?.Dispose();
                state.FlashTimer = null;
            }
        }
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (Volatile.Read(ref _disposed) == 0) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops the flash timers. Running scripts keep running.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var (_, state) in _states)
            lock (state)
            {
                state.FlashTimer?.Dispose();
                state.FlashTimer = null;
            }
    }
}
