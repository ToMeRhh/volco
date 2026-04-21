using System.Runtime.InteropServices;

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
            // Use Span to find the start marker without allocations or excessive indexing
            var span = CollectionsMarshal.AsSpan(_buffer);
            var startIndex = span.IndexOf(StartMarker);

            if (startIndex < 0)
            {
                _buffer.Clear();
                return;
            }
            if (startIndex > 0)
            {
                _buffer.RemoveRange(0, startIndex);
                span = CollectionsMarshal.AsSpan(_buffer);
            }

            // Need at least: marker + length
            if (span.Length < 2)
                return;

            int payloadLength = span[1];
            int frameLength = 2 + payloadLength + 1; // marker + length + payload + checksum

            if (span.Length < frameLength)
                return;

            // Validate checksum using Span for efficiency
            byte checksum = 0;
            var payloadSlice = span.Slice(2, payloadLength);
            foreach (var b in payloadSlice)
                checksum ^= b;

            if (checksum != span[2 + payloadLength])
            {
                // Bad checksum — skip this start marker and try next
                _buffer.RemoveAt(0);
                continue;
            }

            // Parse the payload directly from the Span to avoid allocations (GetRange/ToArray)
            var message = ParsePayload(payloadSlice);

            // IMPORTANT: Remove from buffer AFTER parsing but BEFORE invoking event,
            // as the event handler might (unlikely but possible) re-enter or rely on state.
            // Actually, removing first is safer for recursion, but we must ensure we don't
            // use the span after the list is modified as it might invalidate the span.
            _buffer.RemoveRange(0, frameLength);

            if (message != null)
                MessageParsed?.Invoke(this, message);
        }
    }

    private static ControllerMessage? ParsePayload(ReadOnlySpan<byte> payload)
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
