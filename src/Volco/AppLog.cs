using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Volco;

public static class AppLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Volco", "volco.log");

    private static readonly ConcurrentQueue<string> Queue = new();
    private static readonly Timer FlushTimer;


    static AppLog()
    {
        var dir = Path.GetDirectoryName(LogPath)!;
        Directory.CreateDirectory(dir);
        FlushTimer = new Timer(_ => Flush(), null, 500, 500);
    }

    public static bool IsDebugEnabled { get; set; } = false;

    [InterpolatedStringHandler]
    public ref struct DebugLogHandler
    {
        private DefaultInterpolatedStringHandler _innerHandler;

        public DebugLogHandler(int literalLength, int formattedCount, out bool isEnabled)
        {
            isEnabled = IsDebugEnabled;
            if (isEnabled)
            {
                _innerHandler = new DefaultInterpolatedStringHandler(literalLength, formattedCount);
            }
            else
            {
                _innerHandler = default;
            }
        }

        public void AppendLiteral(string s) => _innerHandler.AppendLiteral(s);
        public void AppendFormatted<T>(T value) => _innerHandler.AppendFormatted(value);
        public void AppendFormatted<T>(T value, string? format) => _innerHandler.AppendFormatted(value, format);
        public void AppendFormatted<T>(T value, int alignment) => _innerHandler.AppendFormatted(value, alignment);
        public void AppendFormatted<T>(T value, int alignment, string? format) => _innerHandler.AppendFormatted(value, alignment, format);

        internal string ToStringAndClear() => _innerHandler.ToStringAndClear();
    }

    public static void Info(string message) => Enqueue("INFO", message);

    public static void Debug(string message)
    {
        if (IsDebugEnabled) Enqueue("DEBUG", message);
    }

    public static void Debug(ref DebugLogHandler handler)
    {
        if (IsDebugEnabled)
            Enqueue("DEBUG", handler.ToStringAndClear());
    }

    public static void Error(string message) => Enqueue("ERROR", message);

    private static void Enqueue(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        Queue.Enqueue(line);
    }

    private static void Flush()
    {
        if (Queue.IsEmpty) return;

        var lines = new List<string>();
        while (Queue.TryDequeue(out var line))
            lines.Add(line);

        if (lines.Count > 0)
        {
            try
            {
                File.AppendAllLines(LogPath, lines);
            }
            catch { }
        }
    }
}
