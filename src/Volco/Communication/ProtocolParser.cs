namespace Volco.Communication;

public class ProtocolParser
{
    private const byte StartMarker = 0xAA;
    private readonly List<byte> _buffer = new();

    public event EventHandler<ControllerMessage>? MessageParsed;

    public void FeedBytes(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            _buffer.Add(b);
        }
        TryParse();
    }

    private void TryParse()
    {
        while (true)
        {
            // Find start marker
            var startIndex = _buffer.IndexOf(StartMarker);
            if (startIndex < 0)
            {
                _buffer.Clear();
                return;
            }
            if (startIndex > 0)
                _buffer.RemoveRange(0, startIndex);

            // Need at least: marker + length
            if (_buffer.Count < 2)
                return;

            int payloadLength = _buffer[1];
            int frameLength = 2 + payloadLength + 1; // marker + length + payload + checksum

            if (_buffer.Count < frameLength)
                return;

            // Validate checksum
            byte checksum = 0;
            for (int i = 2; i < 2 + payloadLength; i++)
                checksum ^= _buffer[i];

            if (checksum != _buffer[2 + payloadLength])
            {
                // Bad checksum — skip this start marker and try next
                _buffer.RemoveAt(0);
                continue;
            }

            // Extract payload and parse
            var payload = _buffer.GetRange(2, payloadLength).ToArray();
            _buffer.RemoveRange(0, frameLength);

            var message = ParsePayload(payload);
            if (message != null)
                MessageParsed?.Invoke(this, message);
        }
    }

    private static ControllerMessage? ParsePayload(byte[] payload)
    {
        if (payload.Length < 3)
            return null;

        var type = (MessageType)payload[0];
        return type switch
        {
            MessageType.Slider when payload.Length >= 4 =>
                new SliderMessage(payload[1], (ushort)((payload[2] << 8) | payload[3])),
            MessageType.Button =>
                new ButtonMessage(payload[1], (ButtonState)payload[2]),
            _ => null
        };
    }
}
