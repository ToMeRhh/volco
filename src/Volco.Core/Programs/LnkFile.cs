using System.Buffers.Binary;
using System.Text;

namespace Volco.Core.Programs;

/// <summary>
/// Reads a Windows shortcut's local target path (MS-SHLLINK: the LinkInfo's LocalBasePath plus
/// CommonPathSuffix), so a shortcut to a game can be matched against running processes. Plain
/// parsing, no shell COM: it runs, and is tested, anywhere. Anything else (a network target, an
/// IDList-only shortcut, a malformed file) gives null, and the program is simply not tracked.
/// </summary>
public static class LnkFile
{
    private const int HeaderSize = 0x4C;
    private const uint HasLinkTargetIdList = 0x1;
    private const uint HasLinkInfo = 0x2;
    private const uint ForceNoLinkInfo = 0x100;
    private const uint VolumeIdAndLocalBasePath = 0x1;

    /// <summary>The shortcut's target, or null when the file cannot be read or has no local target.</summary>
    public static string? TryReadTarget(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 1 << 20) return null;
            return TryReadTarget(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static string? TryReadTarget(byte[] bytes)
    {
        try
        {
            var span = bytes.AsSpan();
            if (span.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(span) != HeaderSize) return null;
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(span[20..]);
            if ((flags & HasLinkInfo) == 0 || (flags & ForceNoLinkInfo) != 0) return null;

            var offset = HeaderSize;
            if ((flags & HasLinkTargetIdList) != 0)
                offset += 2 + BinaryPrimitives.ReadUInt16LittleEndian(span[offset..]);

            var info = span[offset..];
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(info);
            var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(info[4..]);
            if (size < 0x1C || size > info.Length) return null;
            info = info[..size];
            var infoFlags = BinaryPrimitives.ReadUInt32LittleEndian(info[8..]);
            if ((infoFlags & VolumeIdAndLocalBasePath) == 0) return null;

            string? basePath, suffix;
            if (headerSize >= 0x24)
            {
                basePath = Utf16At(info, BinaryPrimitives.ReadUInt32LittleEndian(info[0x1C..]));
                suffix = Utf16At(info, BinaryPrimitives.ReadUInt32LittleEndian(info[0x20..]));
            }
            else
            {
                basePath = AnsiAt(info, BinaryPrimitives.ReadUInt32LittleEndian(info[0x10..]));
                suffix = AnsiAt(info, BinaryPrimitives.ReadUInt32LittleEndian(info[0x18..]));
            }
            if (string.IsNullOrEmpty(basePath)) return null;
            return basePath + (suffix ?? "");
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? AnsiAt(ReadOnlySpan<byte> info, uint offset)
    {
        if (offset == 0 || offset >= info.Length) return null;
        var rest = info[(int)offset..];
        var end = rest.IndexOf((byte)0);
        if (end < 0) return null;
        // The shortcut's ANSI code page is unknown here; Latin-1 keeps ASCII paths exact.
        return Encoding.Latin1.GetString(rest[..end]);
    }

    private static string? Utf16At(ReadOnlySpan<byte> info, uint offset)
    {
        if (offset == 0 || offset >= info.Length) return null;
        var rest = info[(int)offset..];
        for (int i = 0; i + 1 < rest.Length; i += 2)
            if (rest[i] == 0 && rest[i + 1] == 0) return Encoding.Unicode.GetString(rest[..i]);
        return null;
    }
}
