using Volco.Core.Scripts;

namespace Volco.Core.Tests.Fakes;

/// <summary>A process starter whose runs end when the test says so.</summary>
public sealed class FakeProcessStarter : IProcessStarter
{
    private readonly object _lock = new();
    private readonly List<(ProcessSpec Spec, FakeRun Run)> _runs = new();

    /// <summary>Thrown by <see cref="Start"/> when set (an interpreter that is not installed).</summary>
    public Exception? StartFails { get; set; }

    public IReadOnlyList<(ProcessSpec Spec, FakeRun Run)> Runs { get { lock (_lock) return _runs.ToList(); } }

    public IRunningProcess Start(ProcessSpec spec)
    {
        if (StartFails is { } ex) throw ex;
        var run = new FakeRun();
        lock (_lock) _runs.Add((spec, run));
        return run;
    }

    public sealed class FakeRun : IRunningProcess
    {
        private readonly TaskCompletionSource<int?> _exit = new();
        public List<string> Output { get; } = new();
        public Task<int?> Exited => _exit.Task;
        public IReadOnlyList<string> OutputTail => Output;
        public void End(int? code) => _exit.SetResult(code);
    }
}
