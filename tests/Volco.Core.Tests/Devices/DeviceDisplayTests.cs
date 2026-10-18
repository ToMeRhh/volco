using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Devices;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.Core.Tests.Devices;

public class DeviceDisplayTests
{
    private static readonly AudioDevice Speakers =
        new("spk", "Speakers (Realtek High Definition Audio)", AudioFlow.Render, true, DeviceFormFactor.Speakers);
    private static readonly AudioDevice Headset =
        new("hs", "Headset Earphone (Arctis Nova 7)", AudioFlow.Render, false, DeviceFormFactor.Headset);
    private static readonly AudioDevice Mic =
        new("mic", "Microphone (Yeti Stereo Microphone)", AudioFlow.Capture, true, DeviceFormFactor.Microphone);

    private readonly FakeAudioEngine _audio = new() { Devices = { Speakers, Headset, Mic } };

    [Theory]
    [InlineData("Speakers (Realtek High Definition Audio)", "Speakers")]
    [InlineData("Speakers (Realtek(R) Audio)", "Speakers")]
    [InlineData("רמקולים (USB Audio Device)", "רמקולים")]
    [InlineData("(Only parentheses)", "(Only parentheses)")]
    [InlineData("LG HDR 4K", "LG HDR 4K")]
    [InlineData("  Padded (x)  ", "Padded")]
    public void AutoShortName_DropsATrailingParenthesisedPart(string windows, string expected)
        => Assert.Equal(expected, DeviceDisplay.AutoShortName(windows));

    [Fact]
    public void LiveDevice_WithoutSettings_UsesShortNameAndFormFactorIcon()
    {
        var look = DeviceDisplay.For(AppConfig.CreateDefault(), Headset);
        Assert.Equal(new DeviceLook("Headset Earphone", ScreenIcon.Headset), look);
    }

    [Fact]
    public void Nickname_AndChosenIcon_Win()
    {
        var c = AppConfig.CreateDefault();
        c.Devices.Add(new DeviceSettingsConfig { Id = "SPK", Nickname = " Desk speakers ", Icon = ScreenIcon.Speakers });
        Assert.Equal(new DeviceLook("Desk speakers", ScreenIcon.Speakers), DeviceDisplay.For(c, Speakers));
    }

    [Fact]
    public void UnknownFormFactor_IsGeneric()
        => Assert.Equal(ScreenIcon.Generic, DeviceDisplay.IconFor(DeviceFormFactor.Other));

    [Fact]
    public void Reference_ResolvesTheLiveDeviceById_ThenByName()
    {
        var c = AppConfig.CreateDefault();
        Assert.Equal("Speakers", DeviceDisplay.For(c, _audio, new DeviceRefConfig { Id = "spk", FriendlyName = "old" })!.Value.Name);
        Assert.Equal("Microphone", DeviceDisplay.For(c, _audio,
            new DeviceRefConfig { Id = "", FriendlyName = "Microphone (Yeti Stereo Microphone)" })!.Value.Name);
    }

    [Fact]
    public void DisconnectedReference_UsesStoredSettings_ThenTheReferenceName()
    {
        var c = AppConfig.CreateDefault();
        c.Devices.Add(new DeviceSettingsConfig { Id = "sony", FriendlyName = "Headphones (WH-1000XM4)", Icon = ScreenIcon.Headphones });
        Assert.Equal(new DeviceLook("Headphones", ScreenIcon.Headphones),
            DeviceDisplay.For(c, _audio, new DeviceRefConfig { Id = "sony", FriendlyName = "ignored" }));
        Assert.Equal(new DeviceLook("Gone", ScreenIcon.Generic),
            DeviceDisplay.For(c, _audio, new DeviceRefConfig { Id = "x", FriendlyName = "Gone (USB)" }));
    }

    [Fact]
    public void EmptyReference_IsNull()
    {
        var c = AppConfig.CreateDefault();
        Assert.Null(DeviceDisplay.For(c, _audio, null));
        Assert.Null(DeviceDisplay.For(c, _audio, new DeviceRefConfig()));
    }

    [Fact]
    public void NameOf_FallsBackWhenTheReferenceIsEmpty()
        => Assert.Equal("Output", DeviceDisplay.NameOf(AppConfig.CreateDefault(), _audio, null, "Output"));

    [Fact]
    public void CaptureDevice_WithUnknownFormFactor_GetsMic()
    {
        var line = new AudioDevice("line", "Line In (Realtek)", AudioFlow.Capture, false);
        Assert.Equal(ScreenIcon.Mic, DeviceDisplay.For(new AppConfig(), line).Icon);
        _audio.Devices.Add(line);
        Assert.Equal(ScreenIcon.Mic, DeviceDisplay.For(new AppConfig(), _audio, new DeviceRefConfig { Id = "line" })!.Value.Icon);
    }

    [Fact]
    public void RenderDevice_WithUnknownFormFactor_StaysGeneric()
        => Assert.Equal(ScreenIcon.Generic, DeviceDisplay.For(new AppConfig(), new AudioDevice("x", "Thing", AudioFlow.Render, false)).Icon);

    [Fact]
    public void CaptureDevice_WithAChosenIcon_KeepsIt()
    {
        var config = new AppConfig();
        config.Devices.Add(new DeviceSettingsConfig { Id = "line", Nickname = "Aux", Icon = ScreenIcon.Headset });
        Assert.Equal(ScreenIcon.Headset, DeviceDisplay.For(config, new AudioDevice("line", "Line In", AudioFlow.Capture, false)).Icon);
    }

    [Fact]
    public void FlowFromEndpointId_RenderCaptureUnknown()
    {
        Assert.Equal(AudioFlow.Render, DeviceDisplay.FlowFromEndpointId("{0.0.0.00000000}.{0f8a1b2c-0000-4000-8000-000000000001}"));
        Assert.Equal(AudioFlow.Capture, DeviceDisplay.FlowFromEndpointId("{0.0.1.00000000}.{0f8a1b2c-0000-4000-8000-000000000002}"));
        Assert.Null(DeviceDisplay.FlowFromEndpointId("mic-1"));
        Assert.Null(DeviceDisplay.FlowFromEndpointId(""));
        Assert.Null(DeviceDisplay.FlowFromEndpointId(null));
    }

    [Fact]
    public void UnpluggedCaptureId_WithoutSettings_GetsMicAndCaptureFlow()
    {
        var reference = new DeviceRefConfig { Id = "{0.0.1.00000000}.{gone}", FriendlyName = "Microphone (Gone)" };
        Assert.Equal(ScreenIcon.Mic, DeviceDisplay.For(new AppConfig(), _audio, reference)!.Value.Icon);
        Assert.Equal(AudioFlow.Capture, DeviceDisplay.FlowOf(new AppConfig(), _audio, reference));
        Assert.Equal(AudioFlow.Capture, DeviceDisplay.FlowOf(new AppConfig(), _audio, new DeviceRefConfig { Id = "mic" }));
        Assert.Null(DeviceDisplay.FlowOf(new AppConfig(), _audio, new DeviceRefConfig { Id = "unknown" }));
    }
}
