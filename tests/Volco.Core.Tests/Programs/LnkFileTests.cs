using System.Buffers.Binary;
using System.Text;
using Volco.Core.Programs;
using Xunit;

namespace Volco.Core.Tests.Programs;

public class LnkFileTests
{
    /// <summary>A minimal MS-SHLLINK file: header, optional IDList, LinkInfo with a local base
    /// path and common path suffix (ANSI, or Unicode with a 0x24-byte LinkInfo header).</summary>
    internal static byte[] Build(string basePath, string suffix = "", bool idList = false, bool unicode = false,
                                 uint linkInfoFlags = 1)
    {
        var bytes = new List<byte>();
        var header = new byte[0x4C];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x4C);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), (idList ? 1u : 0u) | 2u);
        bytes.AddRange(header);
        if (idList)
        {
            bytes.AddRange(new byte[] { 4, 0 });           // IDListSize = 4
            bytes.AddRange(new byte[] { 0, 0, 0, 0 });     // an empty IDList (TerminalID only)
        }

        var headerSize = unicode ? 0x24 : 0x1C;
        var volumeId = new byte[0x11];
        BinaryPrimitives.WriteUInt32LittleEndian(volumeId, 0x11);
        var ansiBase = Encoding.Latin1.GetBytes(basePath + "\0");
        var ansiSuffix = Encoding.Latin1.GetBytes(suffix + "\0");
        var uniBase = Encoding.Unicode.GetBytes(basePath + "\0");
        var uniSuffix = Encoding.Unicode.GetBytes(suffix + "\0");

        var volumeOffset = headerSize;
        var baseOffset = volumeOffset + volumeId.Length;
        var suffixOffset = baseOffset + ansiBase.Length;
        var uniBaseOffset = suffixOffset + ansiSuffix.Length;
        var uniSuffixOffset = uniBaseOffset + uniBase.Length;
        var size = unicode ? uniSuffixOffset + uniSuffix.Length : uniBaseOffset;

        var info = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(info, (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(4), (uint)headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(8), linkInfoFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(0x0C), (uint)volumeOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(0x10), (uint)baseOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(0x18), (uint)suffixOffset);
        volumeId.CopyTo(info, volumeOffset);
        // A Unicode shortcut keeps an ANSI copy too; make it differ so the test sees which is read.
        (unicode ? Encoding.Latin1.GetBytes("X:\\wrong\0") : ansiBase).CopyTo(info, baseOffset);
        ansiSuffix.CopyTo(info, suffixOffset);
        if (unicode)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(0x1C), (uint)uniBaseOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(0x20), (uint)uniSuffixOffset);
            uniBase.CopyTo(info, uniBaseOffset);
            uniSuffix.CopyTo(info, uniSuffixOffset);
        }
        bytes.AddRange(info);
        return bytes.ToArray();
    }

    [Fact]
    public void ReadsAnsiLocalBasePath()
        => Assert.Equal(@"C:\Games\Doom\doom.exe", LnkFile.TryReadTarget(Build(@"C:\Games\Doom\doom.exe")));

    [Fact]
    public void AppendsTheCommonPathSuffix_AndSkipsTheIdList()
        => Assert.Equal(@"C:\Games\Doom\doom.exe", LnkFile.TryReadTarget(Build(@"C:\Games\", @"Doom\doom.exe", idList: true)));

    [Fact]
    public void PrefersTheUnicodePath()
        => Assert.Equal(@"C:\Spiele\Über\game.exe", LnkFile.TryReadTarget(Build(@"C:\Spiele\Über\game.exe", unicode: true)));

    [Fact]
    public void NoLocalPath_IsNull()
        => Assert.Null(LnkFile.TryReadTarget(Build(@"C:\x.exe", linkInfoFlags: 2)));

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(80)]
    public void TruncatedOrForeignBytes_AreNull(int length)
    {
        var full = Build(@"C:\Games\doom.exe");
        Assert.Null(LnkFile.TryReadTarget(full.Take(length).ToArray()));
        Assert.Null(LnkFile.TryReadTarget(Encoding.ASCII.GetBytes("[InternetShortcut]\nURL=steam://rungameid/1\n")));
    }

    [Fact]
    public void MissingFile_IsNull()
        => Assert.Null(LnkFile.TryReadTarget(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid()}.lnk")));
}
