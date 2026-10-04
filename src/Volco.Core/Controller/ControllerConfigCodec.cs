using Volco.Core.Controller.Proto;

namespace Volco.Core.Controller;

/// <summary>A blob that isn't a valid controller config (spec §6.1): malformed protobuf, a count
/// or string over its limit, invalid UTF-8, or a NUL in a string.</summary>
public sealed class ControllerConfigFormatException : Exception
{
    public ControllerConfigFormatException(string message, Exception? inner = null) : base(message, inner) { }
}

public static class ControllerConfigCodec
{
    public const int MaxBlob = 1024;
    public const int MaxSliders = 16;
    public const int MaxButtons = 32;
    public const int MaxPresetBytes = 24;
    public const int MaxSsidBytes = 32;
    public const int MaxPasswordBytes = 64;
    public const string BlankPresetId = "blank";

    public static byte[] Encode(ControllerConfig config) => throw new NotImplementedException();
    public static ControllerConfig Decode(byte[] blob) => throw new NotImplementedException();
    public static uint Crc32(ReadOnlySpan<byte> data) => throw new NotImplementedException();
}
