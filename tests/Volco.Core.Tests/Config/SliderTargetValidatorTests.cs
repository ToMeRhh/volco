using Volco.Core.Config;
using Xunit;

namespace Volco.Core.Tests.Config;

public class SliderTargetValidatorTests
{
    [Fact]
    public void Processes_Empty()
    {
        var slider = new SliderConfig { Type = SliderMappingType.Processes };
        Assert.Equal("Tick at least one app, or choose a device.", SliderTargetValidator.Validate(slider));
    }

    [Fact]
    public void Processes_One()
    {
        var slider = new SliderConfig { Type = SliderMappingType.Processes, Processes = { "spotify.exe" } };
        Assert.Null(SliderTargetValidator.Validate(slider));
    }

    [Fact]
    public void Device_Null()
    {
        var slider = new SliderConfig { Type = SliderMappingType.Device, Device = null };
        Assert.Null(SliderTargetValidator.Validate(slider));
    }

    [Fact]
    public void Unmapped()
    {
        var slider = new SliderConfig { Type = SliderMappingType.Unmapped };
        Assert.Null(SliderTargetValidator.Validate(slider));
    }
}
