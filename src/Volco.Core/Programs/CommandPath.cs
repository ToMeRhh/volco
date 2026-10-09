using Volco.Core.Config;

namespace Volco.Core.Programs;

/// <summary>
/// Windows path text for a <see cref="CommandConfig"/>, parsed the same on every OS (System.IO.Path
/// treats a backslash as an ordinary character on Linux, where the tests run), and the name a
/// program or script shows on screens and cards.
/// </summary>
public static class CommandPath
{
    private static readonly char[] Separators = { '\\', '/' };

    /// <summary>A URL such as <c>steam://rungameid/570</c>: opened by the shell, never tracked.</summary>
    public static bool IsUrl(string path) => path.Contains("://", StringComparison.Ordinal);

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
        var path = command.Path.Trim();
        if (path.Length == 0) return "";
        if (IsUrl(path)) return path[(path.IndexOf("://", StringComparison.Ordinal) + 3)..].TrimEnd('/');
        return FileNameWithoutExtension(path);
    }
}
