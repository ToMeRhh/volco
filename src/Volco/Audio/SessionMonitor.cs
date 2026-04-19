namespace Volco.Audio;

public class SessionMonitor : IDisposable
{
    private readonly IAudioManager _audioManager;
    private readonly Timer _timer;
    private HashSet<string> _activeProcesses = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> ActiveProcesses => _activeProcesses;

    public SessionMonitor(IAudioManager audioManager, int pollIntervalMs = 2000)
    {
        _audioManager = audioManager;
        _timer = new Timer(_ => Refresh(), null, 0, pollIntervalMs);
    }

    public void Refresh()
    {
        var names = _audioManager.GetActiveProcessNames();
        _activeProcesses = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _timer.Dispose();
    }
}
