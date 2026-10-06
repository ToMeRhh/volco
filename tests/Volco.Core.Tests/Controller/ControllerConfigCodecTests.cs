using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Xunit;

namespace Volco.Core.Tests.Controller;

public class ControllerConfigCodecTests
{
    [Fact]
    public void Encode_BlankConfig_IsNeverEmptyAndCarriesTheBlankPreset()
    {
        var blob = ControllerConfigCodec.Encode(new ControllerConfig());
        Assert.NotEmpty(blob);
        Assert.Equal(ControllerConfigCodec.BlankPresetId, ControllerConfigCodec.Decode(blob).Preset);
    }

    [Fact]
    public void Encode_DoesNotChangeTheCallersConfig()
    {
        var config = new ControllerConfig();
        ControllerConfigCodec.Encode(config);
        Assert.False(config.HasPreset);
    }

    [Fact]
    public void Encode_KeepsAnExplicitPreset()
    {
        var blob = ControllerConfigCodec.Encode(new ControllerConfig { Preset = "diy-reference" });
        Assert.Equal("diy-reference", ControllerConfigCodec.Decode(blob).Preset);
    }

    [Fact]
    public void Encode_RejectsWhatTheFirmwareCouldNotDecode()
    {
        // Validation skips UDP fields when UDP is off, but the firmware's decoder still enforces the
        // limits, so Encode must refuse anything it would answer with DECODE_FAILED.
        var longPassword = new ControllerConfig { Udp = new Udp { Enabled = false, Password = new string('p', 65) } };
        var nulSsid = new ControllerConfig { Udp = new Udp { Ssid = "a\0b" } };
        var longPreset = new ControllerConfig { Preset = new string('x', 25) };
        var tooManyButtons = new ControllerConfig();
        for (var i = 0; i < 33; i++) tooManyButtons.Buttons.Add(new Button { Pin = 4 });

        Assert.Throws<ControllerConfigFormatException>(() => ControllerConfigCodec.Encode(longPassword));
        Assert.Throws<ControllerConfigFormatException>(() => ControllerConfigCodec.Encode(nulSsid));
        Assert.Throws<ControllerConfigFormatException>(() => ControllerConfigCodec.Encode(longPreset));
        Assert.Throws<ControllerConfigFormatException>(() => ControllerConfigCodec.Encode(tooManyButtons));
    }

    [Fact]
    public void Decode_RejectsABlobOverTheMaximum()
        => Assert.Throws<ControllerConfigFormatException>(() => ControllerConfigCodec.Decode(new byte[ControllerConfigCodec.MaxBlob + 1]));

    [Fact]
    public void Crc32_MatchesTheZlibCheckValue()
        => Assert.Equal(0xCBF43926u, ControllerConfigCodec.Crc32("123456789"u8));

    [Fact]
    public void Effective_AbsentSubmessages_HoldTheProtoDefaults()
    {
        var c = new ControllerConfig();
        Assert.Null(c.Screens);
        Assert.Equal(255u, c.EffectiveScreens().Brightness);
        Assert.Equal(30u, c.EffectiveInput().DebounceMs);
        Assert.False(c.EffectiveUdp().Enabled);
        Assert.Equal(12345u, c.EffectiveUdp().PcPort);
    }

    [Fact]
    public void Effective_PresentSubmessage_IsReturnedAsIs()
    {
        var c = new ControllerConfig { Screens = new ScreenBehaviour { Brightness = 7 } };
        Assert.Same(c.Screens, c.EffectiveScreens());
    }
}
