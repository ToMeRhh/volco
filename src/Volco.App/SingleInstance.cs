using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Volco.Core.Diagnostics;

namespace Volco.App;

/// <summary>
/// One Volco per user. The first process holds a named mutex and, once it is up, listens on a
/// named pipe; a later process asks it to show its settings window (unless the later one was
/// started in the background) and exits. Both are plain .NET, so every OS head gets it.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    internal const string ShowCommand = "show";

    /// <summary>How long a later process keeps trying to reach the first one, which may still be
    /// starting (its pipe opens after startup) or exiting (its mutex is about to be freed).</summary>
    internal static readonly TimeSpan ClaimTimeout = TimeSpan.FromSeconds(15);

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;

    private SingleInstance(Mutex mutex, string name)
    {
        _mutex = mutex;
        _pipeName = PipeName(name);
    }

    /// <summary>The per-user name: "Volco-" and the user name, reduced to safe characters.</summary>
    public static string DefaultName
    {
        get
        {
            var user = new StringBuilder();
            foreach (var c in Environment.UserName)
                user.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
            return $"Volco-{user}";
        }
    }

    /// <summary>
    /// Becomes the one Volco, or hands over to the one already running: returns the claim when this
    /// process is the first, and null when another one runs (asked to show its window when
    /// <paramref name="showExisting"/>). Call it on the thread that later disposes the claim.
    /// </summary>
    public static SingleInstance? Claim(string name, bool showExisting, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        var limit = timeout ?? ClaimTimeout;
        while (true)
        {
            var mutex = new Mutex(false, MutexName(name));
            bool owned;
            try { owned = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; }   // the last owner died without letting go
            if (owned) return new SingleInstance(mutex, name);
            mutex.Dispose();

            if (!showExisting)
            {
                AppLog.Info("Volco is already running; this background start exits");
                return null;
            }
            var left = limit - deadline.Elapsed;
            if (left <= TimeSpan.Zero)
            {
                AppLog.Error("Volco is already running but did not answer; this start exits");
                return null;
            }
            if (TrySend(name, ShowCommand, TimeSpan.FromMilliseconds(Math.Min(1000, left.TotalMilliseconds))))
            {
                AppLog.Info("Volco is already running; asked it to show its window");
                return null;
            }
            Thread.Sleep(100);   // the first one may be exiting: try for the mutex again
        }
    }

    /// <summary>Starts answering later processes; <paramref name="onShow"/> runs on a pool thread.</summary>
    public void Listen(Action onShow)
    {
        ArgumentNullException.ThrowIfNull(onShow);
        _listener ??= Task.Run(() => ListenAsync(onShow, _stop.Token));
    }

    private async Task ListenAsync(Action onShow, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);
                using var read = CancellationTokenSource.CreateLinkedTokenSource(ct);
                read.CancelAfter(TimeSpan.FromSeconds(2));   // a client that never writes can't block the next
                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(read.Token);
                if (line == ShowCommand) onShow();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                AppLog.Error($"Single-instance listener: {ex.Message}");
                try { await Task.Delay(500, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    internal static bool TrySend(string name, string command, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(name), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(command);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string MutexName(string name) => name + ".instance";
    private static string PipeName(string name) => name + ".show";

    /// <summary>Stops listening and frees the mutex. Call it on the thread that claimed it.</summary>
    public void Dispose()
    {
        _stop.Cancel();
        try { _listener?.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { }
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { }   // another thread: the OS frees it when this one exits
        _mutex.Dispose();
        _stop.Dispose();
    }
}
