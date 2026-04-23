using System.Collections.Concurrent;

namespace Volco;

internal static class AppLog
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

    public static void Info(string message) => Enqueue("INFO", message);
    public static void Debug(string message) => Enqueue("DEBUG", message);
    public static void Error(string message) => Enqueue("ERROR", message);

    private static void Enqueue(string level, string message)
    {
        var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
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
