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
