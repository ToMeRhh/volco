using Volco.Core.Config;
using Volco.Core.Programs;

namespace Volco.Core.Scripts;

/// <summary>How to start one script run: the program, its whole argument string, the working
/// folder, the extra environment, and whether the shell's file association runs it instead
/// (<see cref="UseShell"/>: no captured output).</summary>
public sealed record ProcessSpec(string FileName, string Arguments, string WorkingDirectory, bool UseShell,
                                 IReadOnlyDictionary<string, string> Environment);

/// <summary>
/// Which interpreter runs a script (custom-actions spec §5.1). Fast starters first: cmd for
/// batch files, then pwsh (else Windows PowerShell) without a profile, the py launcher (else
/// python), node, an exe itself; any other file goes through its file association.
/// </summary>
public static class ScriptInterpreters
{
    /// <param name="onPath">The full path of a program on PATH (e.g. "pwsh.exe"), or null.
    /// The app passes <see cref="PathLookup.Find"/>.</param>
    public static ProcessSpec Resolve(CommandConfig command, Func<string, string?> onPath,
                                      IReadOnlyDictionary<string, string> environment)
    {
        var path = command.Path.Trim();
        var args = command.Arguments.Trim();
        var workingDirectory = command.WorkingDirectory.Trim() is { Length: > 0 } wd ? wd : CommandPath.Directory(path);
        string Then(string text) => args.Length == 0 ? text : $"{text} {args}";
        ProcessSpec Run(string file, string arguments) => new(file, arguments, workingDirectory, false, environment);

        switch (CommandPath.Extension(path))
        {
            case ".bat":
            case ".cmd":
                // /s strips the outer quotes, so a quoted path with spaces and quoted arguments survive.
                return Run(onPath("cmd.exe") ?? "cmd.exe", $"/d /s /c \"{Then($"\"{path}\"")}\"");
            case ".ps1":
                return Run(onPath("pwsh.exe") ?? onPath("powershell.exe") ?? "powershell.exe",
                           Then($"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{path}\""));
            case ".py":
                return Run(onPath("py.exe") ?? onPath("python.exe") ?? "python.exe", Then($"\"{path}\""));
            case ".js":
                return Run(onPath("node.exe") ?? "node.exe", Then($"\"{path}\""));
            case ".exe":
                return Run(path, args);
            default:
                return new ProcessSpec(path, args, workingDirectory, true, environment);
        }
    }
}

/// <summary>Finds a program on PATH, as the shell would.</summary>
public static class PathLookup
{
    public static string? Find(string fileName)
    {
        var paths = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in paths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A PATH entry with invalid characters: skip it.
            }
        }
        return null;
    }
}
