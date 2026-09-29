using Volco.Core.Audio;
using Xunit;

namespace Volco.Core.Tests.Audio;

public class AudioPrimitivesTests
{
    [Fact]
    public void DeviceRef_Default_HasDefaultKindAndNoId()
    {
        var r = DeviceRef.Default(AudioFlow.Render, AudioRole.Multimedia);
        Assert.Equal(DeviceRefKind.Default, r.Kind);
        Assert.Equal(AudioFlow.Render, r.Flow);
        Assert.Equal(AudioRole.Multimedia, r.Role);
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
}
