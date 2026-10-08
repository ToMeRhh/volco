using Volco.Core.Config;

namespace Volco.Core.Programs;

/// <summary>
/// Starts programs and finds running ones (custom-actions spec §4): the seam that keeps process
/// inspection and window activation out of Core. Windows: <c>Volco.Windows.Programs.ProgramLauncher</c>.
/// Paths given to it are already resolved (a shortcut's target, see <see cref="ProgramOpener"/>).
/// </summary>
public interface IProgramLauncher
{
    /// <summary>Whether a process runs from the executable <paramref name="path"/>
    /// (case-insensitive). A process it cannot inspect (elevated) does not count.</summary>
    bool IsRunning(string path);

    /// <summary>Brings the main window of the process running <paramref name="path"/> to the
    /// front, restoring it if minimised. False when there is none.</summary>
    bool BringToFront(string path);

    /// <summary>Starts <paramref name="command"/> through the shell (its path may be an exe, a
    /// shortcut, a document or a URL). False, and logged, when it could not.</summary>
    bool Start(CommandConfig command);

    /// <summary>The executables to poll (about every 2 s); replaces the previous set.</summary>
    void Watch(IReadOnlyCollection<string> paths);

    /// <summary>A watched program started or exited. Any thread.</summary>
    event EventHandler? Changed;
}
