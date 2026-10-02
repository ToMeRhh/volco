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
        Assert.Equal(Golden["hello_led_hardware"], ProtocolEncoder.EncodeHello(1, 5,
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
        Assert.Equal(5, hello.SliderCount);
        Assert.Equal(new[] { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair }, hello.Buttons);

        var screens = Assert.IsType<HelloMessage>(Assert.Single(Parse(Golden["hello_screen_controller"])));
        Assert.Equal(Enumerable.Repeat(IndicatorKind.Screen, 4), screens.Buttons);

        Assert.IsType<AliveMessage>(Assert.Single(Parse(Golden["alive"])));

        var slider = Assert.IsType<SliderMessage>(Assert.Single(Parse(Golden["slider_2_512"])));
        Assert.Equal((2, 512), (slider.SliderIndex, (int)slider.Value));

        var button = Assert.IsType<ButtonMessage>(Assert.Single(Parse(Golden["button_1_pressed"])));
        Assert.Equal((1, ButtonState.Pressed), (button.ButtonIndex, button.State));
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
        // Claims 3 buttons but carries 2 kinds: AA 06 01 01 05 03 01 01 cs
        byte[] body = { 0x01, 0x01, 0x05, 0x03, 0x01, 0x01 };
        byte cs = 0; foreach (var b in body) cs ^= b;
        Assert.Empty(Parse(new byte[] { 0xAA, (byte)body.Length }.Concat(body).Append(cs).ToArray()));
    }
}
