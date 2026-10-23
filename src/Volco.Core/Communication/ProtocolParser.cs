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

    private const int MaxFirmwareId = 32;

    /// <summary>Spec §5.2. A version other than this app's is reported version-only, so the link can
    /// say "incompatible"; a version-1 body that doesn't add up (too short, extra bytes, an over-long
    /// firmware id) is dropped, which is also what happens to a pre-stage-4 Hello.</summary>
    private static HelloMessage? ParseHello(ReadOnlySpan<byte> p)
    {
        byte version = p[1];
        if (version != ProtocolVersion.Current) return HelloMessage.VersionOnly(version);
        if (p.Length < 4) return null;
        var chip = (ChipFamily)p[2];
        int fwLen = p[3];
        int i = 4;
        if (fwLen > MaxFirmwareId || p.Length < i + fwLen + 1 + 4 + 2) return null;
        var firmware = System.Text.Encoding.ASCII.GetString(p.Slice(i, fwLen));
        i += fwLen;
        var state = (ControllerConfigState)p[i++];
        uint crc = (uint)(p[i] << 24 | p[i + 1] << 16 | p[i + 2] << 8 | p[i + 3]);
        i += 4;
        int sliders = p[i++];
        int buttons = p[i++];
        if (p.Length != i + buttons) return null;
        var kinds = new IndicatorKind[buttons];
        for (int b = 0; b < buttons; b++) kinds[b] = (IndicatorKind)p[i + b];
        return new HelloMessage(version, chip, firmware, state, crc, sliders, kinds);
    }

    private static ControllerMessage? ParsePayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1)
            return null;

        switch ((MessageType)payload[0])
        {
            case MessageType.Hello when payload.Length >= 2:
                return ParseHello(payload);
            case MessageType.Alive:
                return new AliveMessage();
            case MessageType.Slider when payload.Length >= 4:
                return new SliderMessage(payload[1], (ushort)((payload[2] << 8) | payload[3]));
            case MessageType.Button when payload.Length >= 3:
                return new ButtonMessage(payload[1], (ButtonState)payload[2]);
            case MessageType.ConfigData when payload.Length >= 10:
                return new ConfigDataMessage(payload[1], (ushort)(payload[2] << 8 | payload[3]),
                    (ushort)(payload[4] << 8 | payload[5]),
                    (uint)(payload[6] << 24 | payload[7] << 16 | payload[8] << 8 | payload[9]),
                    payload[10..].ToArray());
            case MessageType.ConfigResult when payload.Length >= 5:
                return new ConfigResultMessage(payload[1], (Volco.Core.Controller.ConfigResultCode)payload[2], payload[3], payload[4]);
            case MessageType.Calibration when payload.Length >= 7:
                return new CalibrationMessage(payload[1], (payload[2] & 1) != 0, (payload[2] & 2) != 0, (payload[2] & 4) != 0,
                    (ushort)(payload[3] << 8 | payload[4]), (ushort)(payload[5] << 8 | payload[6]));
            case MessageType.PinActivity when payload.Length >= 2:
                return new PinActivityMessage(payload[1]);
            case MessageType.I2cScanResult when payload.Length >= 4 && payload.Length >= 4 + payload[3]:
            {
                var addresses = new int[payload[3]];
                for (int a = 0; a < addresses.Length; a++) addresses[a] = payload[4 + a];
                return new I2cScanResultMessage(payload[1], payload[2], addresses);
            }
            default:
                return null;   // PC to controller types, unknown types, or a body too short for its type
        }
    }
}
