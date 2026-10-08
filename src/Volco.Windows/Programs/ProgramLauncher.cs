using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.Threading;
using Volco.Core.Config;
using Volco.Core.Diagnostics;
using Volco.Core.Programs;

namespace Volco.Windows.Programs;

/// <summary>
/// <see cref="IProgramLauncher"/> for Windows (custom-actions spec §4). A program runs when a
/// process with its file name has its image path (QueryFullProcessImageName; an elevated process
/// it cannot open does not count). Watched paths are polled every <see cref="PollInterval"/>;
/// <see cref="IsRunning"/> answers from that cache (the controller display asks on every push).
/// Bringing a window to the front attaches to the foreground thread's input first, since Windows
/// refuses SetForegroundWindow to a background app; when it still refuses, the taskbar button flashes.
/// </summary>
internal sealed class ProgramLauncher : IProgramLauncher, IDisposable
{
    public static TimeSpan PollInterval { get; } = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly Timer _timer;
    private HashSet<string> _watched = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (bool Running, long At)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    public ProgramLauncher()
    {
        _timer = new Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event EventHandler? Changed;

    public bool IsRunning(string path)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(path, out var entry)
                && (_watched.Contains(path) || Environment.TickCount64 - entry.At < PollInterval.TotalMilliseconds))
                return entry.Running;
        }
        var running = FindProcesses(path).Count > 0;
        lock (_gate) _cache[path] = (running, Environment.TickCount64);
        return running;
    }

    public void Watch(IReadOnlyCollection<string> paths)
    {
        lock (_gate)
        {
            _watched = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            if (Volatile.Read(ref _disposed) != 0) return;
            if (_watched.Count == 0) _timer.Change(Timeout.Infinite, Timeout.Infinite);
            else _timer.Change(TimeSpan.Zero, PollInterval);
        }
    }

    public bool Start(CommandConfig command)
    {
        var path = command.Path.Trim();
        try
        {
            var info = new ProcessStartInfo(path, command.Arguments.Trim()) { UseShellExecute = true };
            var folder = command.WorkingDirectory.Trim();
            if (folder.Length == 0 && !CommandPath.IsUrl(path)) folder = CommandPath.Directory(path);
            if (folder.Length > 0) info.WorkingDirectory = folder;
            using var _ = Process.Start(info);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)   // ERROR_CANCELLED: the UAC prompt was declined
        {
            AppLog.Info($"Open a program: {path} was not started (UAC prompt declined)");
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            AppLog.Error($"Open a program: {path} could not start ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
        // The new process shows up in the next polls; look again soon rather than in 2 s.
        lock (_gate)
            if (Volatile.Read(ref _disposed) == 0 && _watched.Count > 0)
                _timer.Change(TimeSpan.FromSeconds(1), PollInterval);
        return true;
    }

    public bool BringToFront(string path)
    {
        var window = FindProcesses(path).Select(p => p.MainWindowHandle).FirstOrDefault(h => h != IntPtr.Zero);
        if (window == IntPtr.Zero) return false;

        if (IsIconic(window)) ShowWindow(window, SW_RESTORE);
        var foreground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var thisThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != thisThread
                       && AttachThreadInput(thisThread, foregroundThread, true);
        bool done;
        try
        {
            BringWindowToTop(window);
            done = SetForegroundWindow(window);
        }
        finally
        {
            if (attached) AttachThreadInput(thisThread, foregroundThread, false);
        }
        if (!done)
        {
            AppLog.Info($"Open a program: Windows kept {path} in the background; flashing its taskbar button");
            var flash = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = window,
                dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG, uCount = 3, dwTimeout = 0,
            };
            FlashWindowEx(ref flash);
        }
        return true;
    }

    private void Poll()
    {
        string[] watched;
        lock (_gate) watched = _watched.ToArray();
        var changed = false;
        foreach (var path in watched)
        {
            var running = FindProcesses(path).Count > 0;
            lock (_gate)
            {
                changed |= !_cache.TryGetValue(path, out var old) || old.Running != running;
                _cache[path] = (running, Environment.TickCount64);
            }
        }
        if (changed && Volatile.Read(ref _disposed) == 0) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The processes whose image is <paramref name="path"/>: candidates by file name,
    /// then each one's full image path.</summary>
    private static List<Process> FindProcesses(string path)
    {
        var found = new List<Process>();
        Process[] candidates;
        try { candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)); }
        catch (InvalidOperationException) { return found; }
        foreach (var process in candidates)
        {
            if (string.Equals(ImagePath((uint)process.Id), path, StringComparison.OrdinalIgnoreCase)) found.Add(process);
            else process.Dispose();
        }
        return found;
    }

    /// <summary>A process's full image path, or "" when it cannot be opened (the same calls as
    /// <c>SessionRegistry.ResolveImageName</c>).</summary>
    private static string ImagePath(uint pid)
    {
        using var process = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process.IsInvalid) return string.Empty;
        Span<char> buffer = stackalloc char[1024];
        uint size = (uint)buffer.Length;
        return PInvoke.QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer[..(int)size].ToString() : string.Empty;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _timer.Dispose();
        }
    }

    // ── user32 (plain DllImport, as Win32WindowEffects) ───────────────────────

    private const int SW_RESTORE = 9;
    private const uint FLASHW_ALL = 3;
    private const uint FLASHW_TIMERNOFG = 12;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public nint hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
}
