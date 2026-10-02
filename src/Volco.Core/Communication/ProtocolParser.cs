using System.Runtime.InteropServices;

namespace Volco.Core.Communication;

/// <summary>
/// Parses incoming bytes from the controller into <see cref="ControllerMessage"/> objects.
/// Optimized for performance by minimizing allocations and using vectorized searches.
/// </summary>
public class ProtocolParser
{
    private const byte StartMarker = 0xAA;
    private readonly List<byte> _buffer = new();

    public event EventHandler<ControllerMessage>? MessageParsed;

    public void FeedBytes(ReadOnlySpan<byte> data)
    {
        // Use optimized AddRange for ReadOnlySpan (available in .NET 8)
        _buffer.AddRange(data);
        TryParse();
    }

    private void TryParse()
    {
        while (true)
        {
            var bufferSpan = CollectionsMarshal.AsSpan(_buffer);

            // Find start marker using vectorized search
            var startIndex = bufferSpan.IndexOf(StartMarker);
            if (startIndex < 0)
            {
                _buffer.Clear();
                return;
            }

            if (startIndex > 0)
            {
                _buffer.RemoveRange(0, startIndex);
                bufferSpan = CollectionsMarshal.AsSpan(_buffer);
            }

            // Need at least: marker + length
            if (bufferSpan.Length < 2)
                return;

            int payloadLength = bufferSpan[1];
            int frameLength = 2 + payloadLength + 1; // marker + length + payload + checksum

            if (bufferSpan.Length < frameLength)
                return;

            // Validate checksum using Span slice to avoid allocations
            byte checksum = 0;
            var payloadSlice = bufferSpan.Slice(2, payloadLength);
            foreach (var b in payloadSlice)
                checksum ^= b;

            if (checksum != bufferSpan[2 + payloadLength])
            {
                // Bad checksum — skip this start marker and try next
                _buffer.RemoveAt(0);
                continue;
            }

            // Parse payload directly from Span slice (zero-allocation)
            // NOTE: ParsePayload must be called before RemoveRange below;
            // payloadSlice is a span into _buffer and is invalidated by the mutation.
            var message = ParsePayload(payloadSlice);

            // Remove the processed frame from the buffer
            _buffer.RemoveRange(0, frameLength);

            if (message != null)
            {
                // Warning: MessageParsed is invoked while TryParse holds a span view of _buffer.
                // Handlers must not call FeedBytes.
                MessageParsed?.Invoke(this, message);
            }
        }
    }

    private static ControllerMessage? ParsePayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1)
            return null;

        switch ((MessageType)payload[0])
        {
            case MessageType.Hello when payload.Length >= 4:
            {
                int buttons = payload[3];
                if (payload.Length < 4 + buttons) return null;
                var kinds = new IndicatorKind[buttons];
                for (int i = 0; i < buttons; i++) kinds[i] = (IndicatorKind)payload[4 + i];
                return new HelloMessage(payload[1], payload[2], kinds);
            }
            case MessageType.Alive:
                return new AliveMessage();
            case MessageType.Slider when payload.Length >= 4:
                return new SliderMessage(payload[1], (ushort)((payload[2] << 8) | payload[3]));
            case MessageType.Button when payload.Length >= 3:
                return new ButtonMessage(payload[1], (ButtonState)payload[2]);
            default:
                return null;   // PC to controller types, unknown types, or a body too short for its type
        }
    }
}
