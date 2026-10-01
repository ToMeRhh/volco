using Volco.Core.Communication;
using Xunit;

namespace Volco.Core.Tests.Communication;

public class ProtocolEncoderTests
{
    [Fact]
    public void EncodeButtonFeedback_Button0On_CorrectFrame()
    {
        var frame = ProtocolEncoder.EncodeButtonFeedback(0, 1);

        // 0xAA | length 3 | 0x05 button value | XOR checksum
        Assert.Equal(new byte[] { 0xAA, 0x03, 0x05, 0x00, 0x01, 0x05 ^ 0x00 ^ 0x01 }, frame);
    }

    [Fact]
    public void EncodeButtonFeedback_Button2ChoiceB_CorrectFrame()
    {
        var frame = ProtocolEncoder.EncodeButtonFeedback(2, 2);

        Assert.Equal(new byte[] { 0xAA, 0x03, 0x05, 0x02, 0x02, 0x05 ^ 0x02 ^ 0x02 }, frame);
    }

    [Fact]
    public void EncodeButtonFeedback_Off_CorrectFrame()
    {
        var frame = ProtocolEncoder.EncodeButtonFeedback(7, 0);

        Assert.Equal(new byte[] { 0xAA, 0x03, 0x05, 0x07, 0x00, 0x05 ^ 0x07 }, frame);
    }

    [Fact]
    public void EncodedFrames_ParseableByProtocolParser()
    {
        // Round-trip: the parser only understands controller → host types, but a host → controller
        // frame must still be a valid frame it skips without crashing.
        var parser = new ProtocolParser();
        var received = new List<ControllerMessage>();
        parser.MessageParsed += (_, msg) => received.Add(msg);

        parser.FeedBytes(ProtocolEncoder.EncodeButtonFeedback(0, 1));

        Assert.Empty(received);
    }
}
