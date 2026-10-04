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
