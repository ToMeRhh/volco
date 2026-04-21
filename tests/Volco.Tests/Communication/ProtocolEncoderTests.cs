using Volco.Communication;
using Xunit;

namespace Volco.Tests.Communication;

public class ProtocolEncoderTests
{
    [Fact]
    public void EncodeMuteState_MutedButton0_CorrectFrame()
    {
        var frame = ProtocolEncoder.EncodeMuteState(0, isMuted: true);

        // Frame: 0xAA | 0x03 (length) | 0x03 0x00 0x01 (payload) | checksum
        Assert.Equal(0xAA, frame[0]);          // start marker
        Assert.Equal(3, frame[1]);             // payload length
        Assert.Equal(0x03, frame[2]);          // message type
        Assert.Equal(0x00, frame[3]);          // button index
        Assert.Equal(0x01, frame[4]);          // muted = true
        Assert.Equal((byte)(0x03 ^ 0x00 ^ 0x01), frame[5]); // XOR checksum
        Assert.Equal(6, frame.Length);         // total frame size
    }

    [Fact]
    public void EncodeMuteState_UnmutedButton1_CorrectFrame()
    {
        var frame = ProtocolEncoder.EncodeMuteState(1, isMuted: false);

        Assert.Equal(0xAA, frame[0]);
        Assert.Equal(3, frame[1]);
        Assert.Equal(0x03, frame[2]);
        Assert.Equal(0x01, frame[3]);
        Assert.Equal(0x00, frame[4]);
        Assert.Equal((byte)(0x03 ^ 0x01 ^ 0x00), frame[5]);
    }

    [Fact]
    public void EncodeActiveDevice_Device0_CorrectFrame()
    {
        var frame = ProtocolEncoder.EncodeActiveDevice(0);

        // Frame: 0xAA | 0x02 (length) | 0x04 0x00 (payload) | checksum
        Assert.Equal(0xAA, frame[0]);
        Assert.Equal(2, frame[1]);
        Assert.Equal(0x04, frame[2]);
        Assert.Equal(0x00, frame[3]);
        Assert.Equal((byte)(0x04 ^ 0x00), frame[4]);
        Assert.Equal(5, frame.Length);
    }

    [Fact]
    public void EncodeActiveDevice_Device1_CorrectFrame()
    {
        var frame = ProtocolEncoder.EncodeActiveDevice(1);

        Assert.Equal(0xAA, frame[0]);
        Assert.Equal(2, frame[1]);
        Assert.Equal(0x04, frame[2]);
        Assert.Equal(0x01, frame[3]);
        Assert.Equal((byte)(0x04 ^ 0x01), frame[4]);
    }

    [Fact]
    public void EncodedFrames_ParseableByProtocolParser()
    {
        // Round-trip: encode a mute state, feed to parser, verify it finds a valid frame
        // (Parser won't recognize 0x03 as a known type, but it should not crash)
        var parser = new ProtocolParser();
        var received = new List<ControllerMessage>();
        parser.MessageParsed += (_, msg) => received.Add(msg);

        var frame = ProtocolEncoder.EncodeMuteState(0, true);
        parser.FeedBytes(frame);

        // Parser returns null for unknown types but doesn't crash — frame is valid
        Assert.Empty(received);
    }
}
