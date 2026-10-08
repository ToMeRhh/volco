using System.Diagnostics;

namespace Volco.Core.Scripts;

/// <summary>
/// Starts script runs as real processes: no window, stdout and stderr captured into a
/// <see cref="ScriptRunner.TailLines"/>-line ring; or, for <see cref="ProcessSpec.UseShell"/>,
/// through the file association with a hidden window. The process is never killed.
/// </summary>
public sealed class SystemProcessStarter : IProcessStarter
{
    public IRunningProcess Start(ProcessSpec spec)
    {
        var info = new ProcessStartInfo(spec.FileName, spec.Arguments)
        {
            UseShellExecute = spec.UseShell,
            WorkingDirectory = spec.WorkingDirectory,
        };
        if (spec.UseShell)
        {
            info.WindowStyle = ProcessWindowStyle.Hidden;
        }
        else
        {
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            foreach (var (key, value) in spec.Environment) info.Environment[key] = value;
        }

        var run = new Run();
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        if (!spec.UseShell)
        {
            process.OutputDataReceived += (_, e) => run.Add(e.Data);
            process.ErrorDataReceived += (_, e) => run.Add(e.Data);
        }
        if (!process.Start())
        {
            // The shell handed the file to a program that was already running: nothing to watch.
            process.Dispose();
            run.Exit.TrySetResult(null);
            return run;
        }
        if (!spec.UseShell)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        // Subscribed only once the streams are being read (Finish waits for them to drain); a
        // run that already ended raised Exited before this, so check once more.
        process.Exited += (_, _) => run.Finish(process);
        if (process.HasExited) run.Finish(process);
        return run;
    }

    private sealed class Run : IRunningProcess
    {
        private readonly Queue<string> _tail = new();
        private int _finishing;
        public readonly TaskCompletionSource<int?> Exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<int?> Exited => Exit.Task;

        public IReadOnlyList<string> OutputTail { get { lock (_tail) return _tail.ToList(); } }

        public void Add(string? line)
        {
            if (line is null) return;
            lock (_tail)
            {
                _tail.Enqueue(line);
                while (_tail.Count > ScriptRunner.TailLines) _tail.Dequeue();
            }
        }

        public void Finish(Process process)
        {
            // Exited and the HasExited check after Start can both get here.
            if (Interlocked.Exchange(ref _finishing, 1) != 0) return;
            int? code;
            try
            {
                // Let the redirected streams drain, so the tail holds the last lines.
                process.WaitForExit();
                code = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                code = null;
            }
            // Not disposed: Start may still be reading HasExited. The handle's finalizer frees it.
            Exit.TrySetResult(code);
        }
    }
}
