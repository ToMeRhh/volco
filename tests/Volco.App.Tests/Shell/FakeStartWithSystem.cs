using Volco.App.Platform;

namespace Volco.App.Tests.Shell;

/// <summary>An OS sign-in setting in memory; <see cref="Refuse"/> makes the next change throw.</summary>
internal sealed class FakeStartWithSystem : IStartWithSystem
{
    public bool IsEnabled { get; set; }
    public bool Refuse { get; set; }
    public List<bool> Calls { get; } = new();

    public void SetEnabled(bool enabled)
    {
        Calls.Add(enabled);
        if (Refuse) throw new UnauthorizedAccessException("Access is denied.");
        IsEnabled = enabled;
    }
}
