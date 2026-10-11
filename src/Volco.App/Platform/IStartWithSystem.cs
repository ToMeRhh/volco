namespace Volco.App.Platform;

/// <summary>
/// Starts Volco when the user signs in. Each OS head registers it its own way (Windows: the
/// per-user Run key; macOS: a login item) and adds <see cref="LaunchOptions.BackgroundFlag"/> to the
/// command line, so a sign-in start stays in the tray. The OS setting is the only record: Volco
/// keeps no copy, so a change made in the OS's own startup settings shows up here.
/// </summary>
public interface IStartWithSystem
{
    /// <summary>Whether Volco starts at sign-in.</summary>
    bool IsEnabled { get; }

    /// <summary>Starts this executable at sign-in, or stops doing so. Throws when the OS refuses.</summary>
    void SetEnabled(bool enabled);
}
