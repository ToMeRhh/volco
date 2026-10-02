using Volco.Core.Audio;
using Xunit;

namespace Volco.Core.Tests.Audio;

public class AudioPrimitivesTests
{
    [Fact]
    public void DeviceRef_DefaultOutput_HasDefaultKindRenderFlowAndNoId()
    {
        var r = DeviceRef.DefaultOutput();
        Assert.Equal(DeviceRefKind.Default, r.Kind);
        Assert.Equal(AudioFlow.Render, r.Flow);
        Assert.Equal("", r.Id);
    }

    [Fact]
    public void DeviceRef_DefaultInput_HasDefaultKindCaptureFlowAndNoId()
    {
        var r = DeviceRef.DefaultInput();
        Assert.Equal(DeviceRefKind.Default, r.Kind);
        Assert.Equal(AudioFlow.Capture, r.Flow);
        Assert.Equal("", r.Id);
    }

    [Fact]
    public void DeviceRef_ById_CarriesTheEndpointId()
    {
        var r = DeviceRef.ById("{0.0.0.00000000}.{abc}", AudioFlow.Render);
        Assert.Equal(DeviceRefKind.ById, r.Kind);
        Assert.Equal("{0.0.0.00000000}.{abc}", r.Id);
    }

    [Fact]
    public void DeviceRef_ByFriendlyName_CarriesTheName()
    {
        var r = DeviceRef.ByFriendlyName("Headphones", AudioFlow.Render);
        Assert.Equal(DeviceRefKind.ByFriendlyName, r.Kind);
        Assert.Equal("Headphones", r.Id);
    }

    [Fact]
    public void DeviceRef_IsAValueType_SoEqualityIsStructural()
    {
        Assert.Equal(DeviceRef.ById("x"), DeviceRef.ById("x"));
        Assert.NotEqual(DeviceRef.ById("x"), DeviceRef.ById("y"));
    }

    [Fact]
    public void AudioDevice_FormFactorDefaultsToUnknown_AndIsPartOfEquality()
    {
        var plain = new AudioDevice("a", "A", AudioFlow.Render, false);
        Assert.Equal(DeviceFormFactor.Unknown, plain.FormFactor);
        Assert.NotEqual(plain, plain with { FormFactor = DeviceFormFactor.Headset });
    }
}
