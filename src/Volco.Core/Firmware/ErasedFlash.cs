using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Volco.Core.Firmware;

/// <summary>
/// Erased NOR flash reads as 0xFF. A probe whose app-region MD5 is that of <c>size</c> bytes of
/// 0xFF found an erased board (esptool's erase_flash, a new module): nothing of Volco's is on it,
/// whatever was seen this run.
/// </summary>
public static class ErasedFlash
{
    private static readonly ConcurrentDictionary<uint, string> Cache = new();

    /// <summary>The MD5 of <paramref name="size"/> bytes of 0xFF, lower-case hex; computed once per size.</summary>
    public static string Md5(uint size) => Cache.GetOrAdd(size, Compute);

    /// <summary>Whether <paramref name="md5"/> is the MD5 of an erased region of <paramref name="size"/> bytes.</summary>
    public static bool Is(string? md5, uint size)
        => md5 is not null && string.Equals(md5, Md5(size), StringComparison.OrdinalIgnoreCase);

    private static string Compute(uint size)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var block = new byte[64 * 1024];
        Array.Fill(block, (byte)0xFF);
        for (long left = size; left > 0; left -= block.Length)
            hash.AppendData(block, 0, (int)Math.Min(left, block.Length));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
