using System.Text.RegularExpressions;
using Volco.Core.Config;

namespace Volco.Core.Programs;

/// <summary>
/// Windows path text for a <see cref="CommandConfig"/>, parsed the same on every OS (System.IO.Path
/// treats a backslash as an ordinary character on Linux, where the tests run), and the name a
/// program or script shows on screens and cards.
/// </summary>
public static partial class CommandPath
{
    private static readonly char[] Separators = { '\\', '/' };

    // A scheme of two or more characters and a colon; "C:" is a drive letter, not a scheme.
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]+:")]
    private static partial Regex UrlScheme();

    /// <summary>A URL or shell target, such as <c>steam://rungameid/570</c>, <c>ms-settings:sound</c>
    /// or <c>shell:AppsFolder\…!App</c>: opened by the shell, never tracked, with no folder.</summary>
    public static bool IsUrl(string path) => UrlScheme().IsMatch(path.Trim());

    /// <summary><paramref name="text"/> trimmed and without the quotes around it (Explorer's
    /// "Copy as path" adds them).</summary>
    public static string Unquote(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length >= 2 && t[0] == '"' && t[^1] == '"') t = t[1..^1].Trim();
        return t;
    }

    /// <summary>A copy of <paramref name="command"/> with its path and start-in folder unquoted,
    /// so a quoted path saved before the settings UI unquoted it still runs; the command itself
    /// when neither changes.</summary>
    public static CommandConfig Unquoted(CommandConfig command)
    {
        var path = Unquote(command.Path);
        var folder = Unquote(command.WorkingDirectory);
        if (path == command.Path && folder == command.WorkingDirectory) return command;
        return new CommandConfig { Path = path, Arguments = command.Arguments, WorkingDirectory = folder, Name = command.Name };
    }

    /// <summary>The file name, without its folder.</summary>
    public static string FileName(string path)
    {
        var trimmed = path.Trim().TrimEnd(Separators);
        var slash = trimmed.LastIndexOfAny(Separators);
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    /// <summary>The extension with its dot, lower case ("" when none).</summary>
    public static string Extension(string path)
    {
        var name = FileName(path);
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? "" : name[dot..].ToLowerInvariant();
    }

    public static string FileNameWithoutExtension(string path)
    {
        var name = FileName(path);
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? name : name[..dot];
    }

    /// <summary>The folder holding <paramref name="path"/>, or "" when it has none.</summary>
    public static string Directory(string path)
    {
        var trimmed = path.Trim();
        var slash = trimmed.LastIndexOfAny(Separators);
        if (slash < 0) return "";
        // "C:\x.bat" → "C:\", not "C:" (which means the drive's current folder).
        return slash == 2 && trimmed.Length > 1 && trimmed[1] == ':' ? trimmed[..3] : trimmed[..slash];
    }

    /// <summary>What a program or script is called on screens and cards: its name, else the file
    /// name without extension, else the URL without its scheme; "" when nothing is chosen.</summary>
    public static string DisplayName(CommandConfig? command)
    {
        if (command is null) return "";
        if (!string.IsNullOrWhiteSpace(command.Name)) return command.Name.Trim();
        var path = Unquote(command.Path);
        if (path.Length == 0) return "";
        if (IsUrl(path))
        {
            var slashes = path.IndexOf("://", StringComparison.Ordinal);
            return (slashes >= 0 ? path[(slashes + 3)..] : path[(path.IndexOf(':') + 1)..]).TrimEnd('/');
        }
        return FileNameWithoutExtension(path);
    }
}
