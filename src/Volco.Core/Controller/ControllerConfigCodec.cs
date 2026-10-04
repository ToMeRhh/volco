using System.Text;
using Google.Protobuf;
using Volco.Core.Controller.Proto;

namespace Volco.Core.Controller;

/// <summary>A blob that isn't a valid controller config (spec §6.1): malformed protobuf, a count
/// or string over its limit, invalid UTF-8, or a NUL in a string.</summary>
public sealed class ControllerConfigFormatException : Exception
{
    public ControllerConfigFormatException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Encodes and decodes the controller config blob (spec §6.1), enforcing the limits the
/// firmware's nanopb code enforces, which Google.Protobuf doesn't know about. "Same config" is
/// "same bytes" (§6.5): callers keep the bytes they sent or read and never re-encode to compare.</summary>
public static class ControllerConfigCodec
{
    public const int MaxBlob = 1024;
    public const int MaxSliders = 16;
    public const int MaxButtons = 32;
    public const int MaxPresetBytes = 24;
    public const int MaxSsidBytes = 32;
    public const int MaxPasswordBytes = 64;
    public const string BlankPresetId = "blank";

    /// <summary>The blob for <paramref name="config"/>. Never empty: a config without a preset is
    /// sent as preset "blank" (an empty write is TOO_LARGE on the controller).</summary>
    public static byte[] Encode(ControllerConfig config)
    {
        var copy = config.Clone();
        if (string.IsNullOrEmpty(copy.Preset)) copy.Preset = BlankPresetId;
        return copy.ToByteArray();
    }

    /// <exception cref="ControllerConfigFormatException">Malformed, over a limit, invalid UTF-8, or a NUL in a string.</exception>
    public static ControllerConfig Decode(byte[] blob)
    {
        if (blob.Length > MaxBlob)
            throw new ControllerConfigFormatException($"The config is {blob.Length} bytes; the maximum is {MaxBlob}.");
        ControllerConfig config;
        try
        {
            config = ControllerConfig.Parser.ParseFrom(blob);
        }
        catch (InvalidProtocolBufferException e)
        {
            throw new ControllerConfigFormatException("Not a valid controller config: " + e.Message, e);
        }
        if (config.Sliders.Count > MaxSliders)
            throw new ControllerConfigFormatException($"{config.Sliders.Count} sliders; the maximum is {MaxSliders}.");
        if (config.Buttons.Count > MaxButtons)
            throw new ControllerConfigFormatException($"{config.Buttons.Count} buttons; the maximum is {MaxButtons}.");
        CheckString(config.Preset, "preset", MaxPresetBytes);
        if (config.Udp is { } udp)
        {
            CheckString(udp.Ssid, "udp.ssid", MaxSsidBytes);
            CheckString(udp.Password, "udp.password", MaxPasswordBytes);
        }
        return config;
    }

    /// <summary>zlib CRC-32 (spec §6.5).</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            c ^= b;
            for (var k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320u & (0u - (c & 1u)));
        }
        return ~c;
    }

    private static void CheckString(string value, string name, int maxBytes)
    {
        if (value.Contains('\0')) throw new ControllerConfigFormatException($"{name} contains a NUL character.");
        var bytes = Encoding.UTF8.GetByteCount(value);
        if (bytes > maxBytes) throw new ControllerConfigFormatException($"{name} is {bytes} bytes; the maximum is {maxBytes}.");
    }
}
