using Volco.Core.Communication;
using Xunit;

namespace Volco.Core.Tests.Communication;

public class ProtocolEncoderTests
{
    private static byte Checksum(byte[] frame)
    {
        byte cs = 0;
        for (int i = 2; i < frame.Length - 1; i++) cs ^= frame[i];
        return cs;
    }

    [Fact]
    public void EncodeLed_FrameShape()
        => Assert.Equal(new byte[] { 0xAA, 0x03, 0x83, 0x07, 0x00, 0x84 }, ProtocolEncoder.EncodeLed(7, 0));

    [Fact]
    public void EncodeScreen_TopTextIsTruncatedTo60Bytes()
    {
        var frame = ProtocolEncoder.EncodeScreen(0, ScreenTransition.None,
            ScreenContent.Text(new string('A', 80), ScreenIcon.Generic, muted: false, "x"));
        Assert.Equal(60, frame[2 + 5]);                       // length byte of the top text
        Assert.Equal(Checksum(frame), frame[^1]);
    }

    [Fact]
    public void EncodeScreen_NoIcon_Is0xFF()
    {
        var frame = ProtocolEncoder.EncodeScreen(0, ScreenTransition.None, ScreenContent.Blank);
        // AA len 84 btn trans layout topKind(0) icon flags mainLen cs
        Assert.Equal(ProtocolEncoder.NoIcon, frame[7]);
    }

    [Fact]
    public void EncodeScreen_LargestMessage_FitsTheFrame()
    {
        var c = ScreenContent.Text(new string('a', 80), ScreenIcon.Generic, muted: true, new string('b', 80));
        var frame = ProtocolEncoder.EncodeScreen(255, ScreenTransition.SlideRight, c);
        Assert.Equal(132, frame.Length);                      // spec §3.3: 129 bytes of type+body
        Assert.Equal(129, frame[1]);
    }

    [Fact]
    public void Roundtrip_ControllerFramesParse()
    {
        var parser = new ProtocolParser();
        var got = new List<ControllerMessage>();
        parser.MessageParsed += (_, m) => got.Add(m);
        parser.FeedBytes(ProtocolEncoder.EncodeSlider(4, 4095));
        parser.FeedBytes(ProtocolEncoder.EncodeButton(0, ButtonState.Released));
        Assert.IsType<SliderMessage>(got[0]);
        Assert.IsType<ButtonMessage>(got[1]);
    }
}
