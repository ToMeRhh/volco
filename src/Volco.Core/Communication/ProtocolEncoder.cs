namespace Volco.Core.Communication;

public static class ProtocolEncoder
{
    private const byte StartMarker = 0xAA;

    /// <summary>
    /// <c>0x05 ButtonFeedback</c>: tells the controller what <paramref name="buttonIndex"/>'s
    /// indicator shows. <paramref name="value"/> 0 = off/unknown, 1..N = on, or choice n (see
    /// <see cref="ButtonFeedback"/>). The firmware decides how its hardware renders it.
    /// </summary>
    public static byte[] EncodeButtonFeedback(int buttonIndex, byte value)
    {
        byte[] payload = { (byte)MessageType.ButtonFeedback, (byte)buttonIndex, value };
        return BuildFrame(payload);
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
