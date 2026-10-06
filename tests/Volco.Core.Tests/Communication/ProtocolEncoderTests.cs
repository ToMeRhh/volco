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
    public void EncodeConfigWrite_MaximalBlob_FiveChunksAt240ByteOffsets()
    {
        var blob = Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray();
        var burst = ProtocolEncoder.EncodeConfigWrite(3, blob);
        var frames = new List<byte[]>();
        for (int i = 0; i < burst.Length; i += burst[i + 1] + 3) frames.Add(burst[i..(i + burst[i + 1] + 3)]);
        Assert.Equal(7, frames.Count);  // Begin, 5 chunks, End
        Assert.Equal(0x88, frames[0][2]);
        Assert.Equal(0x8A, frames[6][2]);
        var offsets = frames.Skip(1).Take(5).Select(f => f[4] << 8 | f[5]).ToArray();
        Assert.Equal(new[] { 0, 240, 480, 720, 960 }, offsets);
        Assert.All(frames, f => Assert.True(f[1] <= 255));
        Assert.Equal(blob, frames.Skip(1).Take(5).SelectMany(f => f[6..^1]).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1025)]
    public void EncodeConfigWrite_RejectsEmptyAndOversizedBlobs(int size)
        => Assert.Throws<ArgumentException>(() => ProtocolEncoder.EncodeConfigWrite(1, new byte[size]));

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
