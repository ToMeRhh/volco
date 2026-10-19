namespace Volco.Core.Audio;

/// <summary>
/// How an audio session is named and which file its icon comes from (controls-page spec §10.3).
/// A session's own display name or icon path that starts with <c>@</c> is an unresolved resource
/// string (e.g. "@%SystemRoot%\System32\AudioSrv.Dll,-202") and is skipped.
/// </summary>
public static class SessionNames
{
    /// <summary>The first non-blank of: the session's display name (unless a resource string),
    /// the exe's file description, the image name without its extension; trimmed.</summary>
    public static string Pick(string sessionDisplayName, string? fileDescription, string imageName)
    {
        var session = (sessionDisplayName ?? "").Trim();
        if (session.Length > 0 && !session.StartsWith('@')) return session;

        var description = (fileDescription ?? "").Trim();
        if (description.Length > 0) return description;

        return Path.GetFileNameWithoutExtension((imageName ?? "").Trim()).Trim();
    }

    /// <summary>The session's icon path (an <c>,index</c> suffix kept) when it is set and not a
    /// resource string, else the exe path, else "".</summary>
    public static string IconSource(string sessionIconPath, string? exePath)
    {
        var session = (sessionIconPath ?? "").Trim();
        if (session.Length > 0 && !session.StartsWith('@')) return session;

        return (exePath ?? "").Trim();
    }
}
