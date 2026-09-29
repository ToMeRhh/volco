namespace Volco.Core.Communication;

public static class ProtocolEncoder
{
    private const byte StartMarker = 0xAA;

    public static byte[] EncodeMuteState(int buttonIndex, bool isMuted)
    {
        byte[] payload = { (byte)MessageType.MuteState, (byte)buttonIndex, (byte)(isMuted ? 1 : 0) };
        return BuildFrame(payload);
    }

    public static byte[] EncodeActiveDevice(int deviceIndex)
    {
        byte[] payload = { (byte)MessageType.ActiveDevice, (byte)deviceIndex };
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
