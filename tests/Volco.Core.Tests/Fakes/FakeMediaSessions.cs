using Volco.Core.Media;

namespace Volco.Core.Tests.Fakes;

/// <summary>In-memory <see cref="IMediaSessions"/>: tests set the apps and read what was sent.</summary>
public sealed class FakeMediaSessions : IMediaSessions
{
    private readonly object _lock = new();
    private List<MediaApp> _apps = new();
    private readonly List<(string? AppId, MediaCommand Command)> _sent = new();

    public string? CurrentAppId { get; set; }

    public IReadOnlyList<MediaApp> GetApps() { lock (_lock) return _apps.ToList(); }

    /// <summary>What <see cref="Send"/> answers (whether the app took the command).</summary>
    public bool Accepts { get; set; } = true;

    public Task<bool> Send(string? appId, MediaCommand command)
    {
        lock (_lock) _sent.Add((appId, command));
        return Task.FromResult(Accepts);
    }

    public IReadOnlyList<(string? AppId, MediaCommand Command)> Sent { get { lock (_lock) return _sent.ToList(); } }

    public event EventHandler? Changed;

    /// <summary>Replaces the apps and raises <see cref="Changed"/>. The first app is current
    /// unless <paramref name="current"/> names another (or null for none).</summary>
    public void SetApps(IEnumerable<MediaApp> apps, string? current = "")
    {
        lock (_lock) _apps = apps.ToList();
        CurrentAppId = current == "" ? _apps.FirstOrDefault()?.Id : current;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
