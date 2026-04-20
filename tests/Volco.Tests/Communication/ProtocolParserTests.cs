using Volco.Communication;
using Xunit;

namespace Volco.Tests.Communication;

public class ProtocolParserTests
{
    private readonly ProtocolParser _parser = new();
    private readonly List<ControllerMessage> _received = new();

    public ProtocolParserTests()
    {
        _parser.MessageParsed += (_, msg) => _received.Add(msg);
    }

    private byte[] FramePayload(byte[] payload)
    {
        byte checksum = 0;
        foreach (var b in payload)
            checksum ^= b;
        var frame = new byte[payload.Length + 3];
        frame[0] = 0xAA;
        frame[1] = (byte)payload.Length;
        Array.Copy(payload, 0, frame, 2, payload.Length);
        frame[^1] = checksum;
        return frame;
    }

    [Fact]
    public void ParsesSliderMessage()
    {
        // Slider 2, value 512 (0x0200 big-endian)
        var payload = new byte[] { 0x01, 0x02, 0x02, 0x00 };
        _parser.FeedBytes(FramePayload(payload));

        Assert.Single(_received);
        var msg = Assert.IsType<SliderMessage>(_received[0]);
        Assert.Equal(2, msg.SliderIndex);
        Assert.Equal(512, msg.Value);
    }

    [Fact]
    public void ParsesButtonMessage()
    {
        // Button 1, pressed
        var payload = new byte[] { 0x02, 0x01, 0x01 };
        _parser.FeedBytes(FramePayload(payload));

        Assert.Single(_received);
        var msg = Assert.IsType<ButtonMessage>(_received[0]);
        Assert.Equal(1, msg.ButtonIndex);
        Assert.Equal(ButtonState.Pressed, msg.State);
    }

    [Fact]
    public void RejectsInvalidChecksum()
    {
        var payload = new byte[] { 0x02, 0x01, 0x01 };
        var frame = FramePayload(payload);
        frame[^1] ^= 0xFF; // corrupt checksum
        _parser.FeedBytes(frame);

        Assert.Empty(_received);
    }

    [Fact]
    public void ParsesMultipleMessagesInOneBuffer()
    {
        var frame1 = FramePayload(new byte[] { 0x01, 0x00, 0x00, 0x64 }); // slider 0, value 100
        var frame2 = FramePayload(new byte[] { 0x02, 0x02, 0x00 });       // button 2, released
        var combined = frame1.Concat(frame2).ToArray();
        _parser.FeedBytes(combined);

        Assert.Equal(2, _received.Count);
        Assert.IsType<SliderMessage>(_received[0]);
        Assert.IsType<ButtonMessage>(_received[1]);
    }

    [Fact]
    public void ParsesMessageSplitAcrossMultipleFeeds()
    {
        var frame = FramePayload(new byte[] { 0x01, 0x03, 0x03, 0xFF }); // slider 3, value 1023
        var half = frame.Length / 2;
        _parser.FeedBytes(frame[..half]);
        Assert.Empty(_received);
        _parser.FeedBytes(frame[half..]);

        Assert.Single(_received);
        var msg = Assert.IsType<SliderMessage>(_received[0]);
        Assert.Equal(3, msg.SliderIndex);
        Assert.Equal(1023, msg.Value);
    }

    [Fact]
    public void SkipsGarbageBytesBeforeStartMarker()
    {
        var garbage = new byte[] { 0x00, 0x55, 0xFF };
        var frame = FramePayload(new byte[] { 0x02, 0x00, 0x01 }); // button 0, pressed
        _parser.FeedBytes(garbage.Concat(frame).ToArray());

        Assert.Single(_received);
        Assert.IsType<ButtonMessage>(_received[0]);
    }
}
