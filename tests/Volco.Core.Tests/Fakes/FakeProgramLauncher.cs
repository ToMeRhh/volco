using Volco.Core.Config;
using Volco.Core.Programs;

namespace Volco.Core.Tests.Fakes;

/// <summary>In-memory <see cref="IProgramLauncher"/>: tests mark paths running and read the calls.</summary>
public sealed class FakeProgramLauncher : IProgramLauncher
{
    private readonly object _lock = new();
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _fronted = new();
    private readonly List<CommandConfig> _started = new();

    public IReadOnlyList<string> Fronted { get { lock (_lock) return _fronted.ToList(); } }
    public IReadOnlyList<CommandConfig> Started { get { lock (_lock) return _started.ToList(); } }
    public IReadOnlyCollection<string> Watched { get; private set; } = Array.Empty<string>();

    /// <summary>What <see cref="Start"/> returns.</summary>
    public bool StartSucceeds { get; set; } = true;

    public bool IsRunning(string path) { lock (_lock) return _running.Contains(path); }

    public bool BringToFront(string path)
    {
        lock (_lock)
        {
            _fronted.Add(path);
            return _running.Contains(path);
        }
    }

    public bool Start(CommandConfig command)
    {
        lock (_lock) _started.Add(command);
        return StartSucceeds;
    }

    public void Watch(IReadOnlyCollection<string> paths) => Watched = paths.ToList();

    public event EventHandler? Changed;

    public void SetRunning(string path, bool running)
    {
        lock (_lock)
        {
            if (running) _running.Add(path);
            else _running.Remove(path);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
