using Volco.Core.Communication;
using Xunit;

namespace Volco.Core.Tests.Communication;

/// <summary>The C# side of the shared golden frames (firmware/test/golden/frames.txt). The
/// firmware's native tests check the same bytes, so the two implementations cannot drift.</summary>
public class ProtocolGoldenTests
{
    private static readonly Dictionary<string, byte[]> Golden = Load();

    private static Dictionary<string, byte[]> Load()
    {
        var result = new Dictionary<string, byte[]>();
        foreach (var raw in File.ReadAllLines(RepoPaths.Combine("firmware", "test", "golden", "frames.txt")))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            result[parts[0]] = parts.Skip(1).Select(h => Convert.ToByte(h, 16)).ToArray();
        }
        return result;
    }

    private static List<ControllerMessage> Parse(byte[] bytes)
    {
        var parser = new ProtocolParser();
        var messages = new List<ControllerMessage>();
        parser.MessageParsed += (_, m) => messages.Add(m);
        parser.FeedBytes(bytes);
        return messages;
    }

    [Fact]
    public void PcMessages_EncodeToTheGoldenBytes()
    {
        Assert.Equal(Golden["hello_request"], ProtocolEncoder.EncodeHelloRequest());
        Assert.Equal(Golden["heartbeat"], ProtocolEncoder.EncodeHeartbeat());
        Assert.Equal(Golden["begin"], ProtocolEncoder.EncodeBegin());
        Assert.Equal(Golden["commit"], ProtocolEncoder.EncodeCommit());
        Assert.Equal(Golden["led_2_value_2"], ProtocolEncoder.EncodeLed(2, 2));
        Assert.Equal(Golden["screen_muted_mic"], ProtocolEncoder.EncodeScreen(1, ScreenTransition.None,
            ScreenContent.Text("MUTED", ScreenIcon.Mic, muted: true, "Microphone")));
        Assert.Equal(Golden["screen_preset_dots_slide_left"], ProtocolEncoder.EncodeScreen(0, ScreenTransition.SlideLeft,
            ScreenContent.Dots(1, 3, ScreenIcon.Presets, "Gaming")));
        Assert.Equal(Golden["screen_blank_slide_right"], ProtocolEncoder.EncodeScreen(2, ScreenTransition.SlideRight,
            ScreenContent.Blank));
        Assert.Equal(Golden["screen_hebrew_main"], ProtocolEncoder.EncodeScreen(3, ScreenTransition.None,
            ScreenContent.Text("NEXT: Speakers", ScreenIcon.Speaker, muted: false, "רמקולים")));
    }

    [Fact]
    public void ControllerMessages_EncodeToTheGoldenBytes()
    {
        Assert.Equal(Golden["hello_led_hardware"], ProtocolEncoder.EncodeHello(1, ChipFamily.Esp32, "dev",
            ControllerConfigState.Stored, 0x12345678, 5,
            new[] { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair }));
        Assert.Equal(Golden["alive"], ProtocolEncoder.EncodeAlive());
        Assert.Equal(Golden["slider_2_512"], ProtocolEncoder.EncodeSlider(2, 512));
        Assert.Equal(Golden["button_1_pressed"], ProtocolEncoder.EncodeButton(1, ButtonState.Pressed));
    }

    [Fact]
    public void ControllerMessages_ParseFromTheGoldenBytes()
    {
        var hello = Assert.IsType<HelloMessage>(Assert.Single(Parse(Golden["hello_led_hardware"])));
        Assert.Equal(1, hello.Version);
        Assert.Equal(ChipFamily.Esp32, hello.Chip);
        Assert.Equal("dev", hello.FirmwareVersion);
        Assert.Equal(ControllerConfigState.Stored, hello.ConfigState);
        Assert.Equal(0x12345678u, hello.ConfigCrc);
        Assert.Equal(5, hello.SliderCount);
        Assert.Equal(new[] { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair }, hello.Buttons);

        var screens = Assert.IsType<HelloMessage>(Assert.Single(Parse(Golden["hello_screen_controller"])));
        Assert.Equal(Enumerable.Repeat(IndicatorKind.Screen, 4), screens.Buttons);
        Assert.Equal((ControllerConfigState.None, 0u), (screens.ConfigState, screens.ConfigCrc));

        var damaged = Assert.IsType<HelloMessage>(Assert.Single(Parse(Golden["hello_damaged"])));
        Assert.Equal((ControllerConfigState.Damaged, 0xCAFEF00Du, 0), (damaged.ConfigState, damaged.ConfigCrc, damaged.Buttons.Count));

        var future = Assert.IsType<HelloMessage>(Assert.Single(Parse(Golden["hello_version_2"])));
        Assert.Equal(2, future.Version);  // version-only: the link reports a version mismatch

        Assert.Empty(Parse(Golden["hello_old_format"]));  // pre-stage-4 firmware: dropped, not misread

        Assert.IsType<AliveMessage>(Assert.Single(Parse(Golden["alive"])));

        var slider = Assert.IsType<SliderMessage>(Assert.Single(Parse(Golden["slider_2_512"])));
        Assert.Equal((2, 512), (slider.SliderIndex, (int)slider.Value));

        var button = Assert.IsType<ButtonMessage>(Assert.Single(Parse(Golden["button_1_pressed"])));
        Assert.Equal((1, ButtonState.Pressed), (button.ButtonIndex, button.State));
    }

    [Fact]
    public void ConfigFrames_EncodeToTheGoldenBytes()
    {
        Assert.Equal(Golden["config_read_seq7"], ProtocolEncoder.EncodeConfigRead(7));
        var burst = Golden["config_write_begin_seq9"].Concat(Golden["config_write_chunk_seq9"])
                                                      .Concat(Golden["config_write_end_seq9"]).ToArray();
        Assert.Equal(burst, ProtocolEncoder.EncodeConfigWrite(9, new byte[] { 0x32, 0x02, 0x08, 0x64 }));
    }

    [Fact]
    public void ConfigFrames_ParseFromTheGoldenBytes()
    {
        var data = Assert.IsType<ConfigDataMessage>(Assert.Single(Parse(Golden["config_data_seq7"])));
        Assert.Equal((7, 3, 0, 0x11223344u), (data.Seq, (int)data.Total, (int)data.Offset, data.Crc32));
        Assert.Equal(new byte[] { 0x32, 0x02, 0x08 }, data.Data);

        var empty = Assert.IsType<ConfigDataMessage>(Assert.Single(Parse(Golden["config_data_empty_seq7"])));
        Assert.Equal((0, 0u), ((int)empty.Total, empty.Crc32));
        Assert.Empty(empty.Data);

        var ok = Assert.IsType<ConfigResultMessage>(Assert.Single(Parse(Golden["config_result_ok_seq9"])));
        Assert.Equal((9, Volco.Core.Controller.ConfigResultCode.Ok, 0xFF, 0xFF), (ok.Seq, ok.Code, (int)ok.Item, (int)ok.Field));
        var invalid = Assert.IsType<ConfigResultMessage>(Assert.Single(Parse(Golden["config_result_invalid_seq9"])));
        Assert.Equal((9, Volco.Core.Controller.ConfigResultCode.Invalid, 2, 7), (invalid.Seq, invalid.Code, (int)invalid.Item, (int)invalid.Field));
    }

    [Fact]
    public void ATwoChunkReadReply_AssemblesToTheBlob()
    {
        var blob = Enumerable.Range(0, 250).Select(i => (byte)(i * 7 + 1)).ToArray();
        var parts = new[] { "config_data_250_seq3_part0", "config_data_250_seq3_part1" }
            .Select(n => Assert.IsType<ConfigDataMessage>(Assert.Single(Parse(Golden[n])))).ToList();
        Assert.Equal(new[] { 0, 240 }, parts.Select(p => (int)p.Offset));
        Assert.All(parts, p => Assert.Equal((3, 250, 0xF6E6129Eu), (p.Seq, (int)p.Total, p.Crc32)));
        var assembled = parts.SelectMany(p => p.Data).ToArray();
        Assert.Equal(blob, assembled);
        Assert.Equal(0xF6E6129Eu, Volco.Core.Controller.ControllerConfigCodec.Crc32(assembled));
    }

    [Fact]
    public void PcOnlyMessages_AreNotParsedAsControllerMessages()
    {
        Assert.Empty(Parse(Golden["heartbeat"]));
        Assert.Empty(Parse(Golden["screen_muted_mic"]));
    }

    [Fact]
    public void HelloWithTooFewKindBytes_IsDropped()
    {
        // A new-format Hello claiming 3 buttons but carrying 2 kinds.
        byte[] body = { 0x01, 0x01, 0x01, 0x00, 0x01, 0, 0, 0, 0, 0x05, 0x03, 0x01, 0x01 };
        byte cs = 0; foreach (var b in body) cs ^= b;
        Assert.Empty(Parse(new byte[] { 0xAA, (byte)body.Length }.Concat(body).Append(cs).ToArray()));
    }
}
