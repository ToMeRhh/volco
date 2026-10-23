namespace Volco.Core.Communication;

/// <summary>
/// Builds wire frames: <c>0xAA | length | type | body | XOR(type, body)</c> (spec §3.1). The PC
/// sends the first group; the controller-side encoders exist for tests, the golden frames and the
/// simulator's expectations.
/// </summary>
public static class ProtocolEncoder
{
    private const byte StartMarker = 0xAA;

    /// <summary>The icon byte for "no icon".</summary>
    public const byte NoIcon = 0xFF;

    // ── PC → controller ─────────────────────────────────────────────────────

    public static byte[] EncodeHelloRequest() => BuildFrame(new[] { (byte)MessageType.HelloRequest });
    public static byte[] EncodeHeartbeat() => BuildFrame(new[] { (byte)MessageType.Heartbeat });
    public static byte[] EncodeBegin() => BuildFrame(new[] { (byte)MessageType.Begin });
    public static byte[] EncodeCommit() => BuildFrame(new[] { (byte)MessageType.Commit });

    /// <summary>Asks for the controller's stored config (spec §5.3); <paramref name="seq"/> is echoed.</summary>
    public static byte[] EncodeConfigRead(byte seq) => BuildFrame(new[] { (byte)MessageType.ConfigRead, seq });

    /// <summary>The largest chunk of a config write or read reply (spec §5.3, §5.4).</summary>
    public const int ConfigChunkSize = 240;

    /// <summary>A whole config write (spec §5.4) as one burst - Begin, the 240-byte chunks, End - so it
    /// is one Send: one serial write, one WebSocket message, one UDP datagram.</summary>
    /// <exception cref="ArgumentException">The blob is empty or over 1024 bytes.</exception>
    public static byte[] EncodeConfigWrite(byte seq, byte[] blob)
    {
        if (blob.Length is 0 or > Volco.Core.Controller.ControllerConfigCodec.MaxBlob)
            throw new ArgumentException($"A config blob is 1..{Volco.Core.Controller.ControllerConfigCodec.MaxBlob} bytes, not {blob.Length}.", nameof(blob));
        var crc = Volco.Core.Controller.ControllerConfigCodec.Crc32(blob);
        var burst = new List<byte>();
        burst.AddRange(BuildFrame(new[]
        {
            (byte)MessageType.ConfigWriteBegin, seq, (byte)(blob.Length >> 8), (byte)blob.Length,
            (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc,
        }));
        for (int offset = 0; offset < blob.Length; offset += ConfigChunkSize)
        {
            int n = Math.Min(ConfigChunkSize, blob.Length - offset);
            var payload = new byte[4 + n];
            payload[0] = (byte)MessageType.ConfigWriteChunk;
            payload[1] = seq;
            payload[2] = (byte)(offset >> 8);
            payload[3] = (byte)offset;
            Array.Copy(blob, offset, payload, 4, n);
            burst.AddRange(BuildFrame(payload));
        }
        burst.AddRange(BuildFrame(new[] { (byte)MessageType.ConfigWriteEnd, seq }));
        return burst.ToArray();
    }

    /// <summary>A slider calibration request (slider-calibration spec, Wire); the controller answers
    /// with that slider's Calibration.</summary>
    public static byte[] EncodeCalibrate(int index, CalibrateAction action)
        => BuildFrame(new[] { (byte)MessageType.Calibrate, (byte)index, (byte)action });

    /// <summary>Starts, renews or stops a pin watch (firmware helpers, Detect); the controller answers
    /// with a PinActivity for each pin that moves.</summary>
    public static byte[] EncodePinWatch(PinWatchRole role)
        => BuildFrame(new[] { (byte)MessageType.PinWatch, (byte)role });

    /// <summary>Scans an I2C bus on these pins (firmware helpers, Find screens); the controller answers
    /// with an I2cScanResult for the same pins.</summary>
    public static byte[] EncodeI2cScan(int sda, int scl)
        => BuildFrame(new[] { (byte)MessageType.I2cScan, (byte)sda, (byte)scl });

    /// <summary>Drives a pin high or low for a while, then restores it (firmware helpers, Try the
    /// lights). The controller ignores a pin it may not drive.</summary>
    public static byte[] EncodePinDrive(int pin, bool high, ushort durationMs)
        => BuildFrame(new[]
        {
            (byte)MessageType.PinDrive, (byte)pin, high ? (byte)1 : (byte)0, (byte)(durationMs >> 8), (byte)durationMs,
        });

    /// <summary>LED: lit when value ≠ 0. LED pair: 1 lights A, 2 lights B, anything else neither.</summary>
    public static byte[] EncodeLed(int button, byte value)
        => BuildFrame(new[] { (byte)MessageType.Led, (byte)button, value });

    /// <summary>One screen in layout 1 (spec §3.3).</summary>
    public static byte[] EncodeScreen(int button, ScreenTransition transition, ScreenContent content)
    {
        var body = new List<byte>(132)
        {
            (byte)MessageType.Screen, (byte)button, (byte)transition, ScreenContent.Layout, (byte)content.TopKind,
        };
        switch (content.TopKind)
        {
            case TopKind.Text:
                AppendText(body, content.TopText);
                break;
            case TopKind.Dots:
                body.Add(content.DotsSelected);
                body.Add(content.DotsCount);
                break;
        }
        body.Add(content.Icon is { } icon ? (byte)icon : NoIcon);
        body.Add(content.Muted ? (byte)1 : (byte)0);
        AppendText(body, content.Main);
        return BuildFrame(body.ToArray());
    }

    // ── Controller → PC (tests, golden frames) ─────────────────────────────

    public static byte[] EncodeHello(byte version, ChipFamily chip, string firmware, ControllerConfigState state,
                                     uint crc, int sliders, IReadOnlyList<IndicatorKind> buttons)
    {
        var fw = System.Text.Encoding.ASCII.GetBytes(firmware);
        var body = new List<byte> { (byte)MessageType.Hello, version, (byte)chip, (byte)fw.Length };
        body.AddRange(fw);
        body.Add((byte)state);
        body.AddRange(new[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc });
        body.Add((byte)sliders);
        body.Add((byte)buttons.Count);
        body.AddRange(buttons.Select(k => (byte)k));
        return BuildFrame(body.ToArray());
    }

    public static byte[] EncodeAlive() => BuildFrame(new[] { (byte)MessageType.Alive });

    public static byte[] EncodeSlider(int index, ushort value)
        => BuildFrame(new[] { (byte)MessageType.Slider, (byte)index, (byte)(value >> 8), (byte)(value & 0xFF) });

    public static byte[] EncodeButton(int index, ButtonState state)
        => BuildFrame(new[] { (byte)MessageType.Button, (byte)index, (byte)state });

    public static byte[] EncodeCalibration(int index, bool learned, bool sweeping, bool autoCalibrate, ushort low, ushort high)
        => BuildFrame(new[]
        {
            (byte)MessageType.Calibration, (byte)index,
            (byte)((learned ? 1 : 0) | (sweeping ? 2 : 0) | (autoCalibrate ? 4 : 0)),
            (byte)(low >> 8), (byte)low, (byte)(high >> 8), (byte)high,
        });

    public static byte[] EncodePinActivity(int pin)
        => BuildFrame(new[] { (byte)MessageType.PinActivity, (byte)pin });

    public static byte[] EncodeI2cScanResult(int sda, int scl, IReadOnlyList<int> addresses)
    {
        var body = new List<byte> { (byte)MessageType.I2cScanResult, (byte)sda, (byte)scl, (byte)addresses.Count };
        body.AddRange(addresses.Select(a => (byte)a));
        return BuildFrame(body.ToArray());
    }

    private static void AppendText(List<byte> body, string text)
    {
        var bytes = ScreenText.ToWireBytes(text);
        body.Add((byte)bytes.Length);
        body.AddRange(bytes);
    }

    private static byte[] BuildFrame(byte[] payload)
    {
        var frame = new byte[payload.Length + 3]; // marker + length + payload + checksum
        frame[0] = StartMarker;
        frame[1] = (byte)payload.Length;
        byte checksum = 0;
        for (int i = 0; i < payload.Length; i++)
        {
            frame[2 + i] = payload[i];
            checksum ^= payload[i];
        }
        frame[^1] = checksum;
        return frame;
    }
}
