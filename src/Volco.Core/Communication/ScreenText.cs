using System.Globalization;
using System.Text;

namespace Volco.Core.Communication;

/// <summary>Text rules for screen slots (spec §3.3): Hebrew combining marks removed (the firmware
/// cannot stack them), then at most 60 UTF-8 bytes, cut on a code-point boundary.</summary>
public static class ScreenText
{
    public const int MaxBytes = 60;

    /// <summary>Removes non-spacing marks in U+0591–U+05C7 (niqqud and cantillation). Punctuation
    /// in that block, like maqaf U+05BE, stays.</summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is >= '֑' and <= 'ׇ'
                && CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>The sanitized text as UTF-8, cut to <see cref="MaxBytes"/> without splitting a
    /// character (a surrogate pair is one rune, so an emoji is never halved).</summary>
    public static byte[] ToWireBytes(string? text)
    {
        var result = new List<byte>(MaxBytes);
        Span<byte> buf = stackalloc byte[4];
        foreach (var rune in Sanitize(text).EnumerateRunes())
        {
            var n = rune.EncodeToUtf8(buf);
            if (result.Count + n > MaxBytes) break;
            for (int i = 0; i < n; i++) result.Add(buf[i]);
        }
        return result.ToArray();
    }
}
