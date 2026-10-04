using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Xunit;

namespace Volco.Core.Tests.Controller;

/// <summary>What the golden vectors can't reach: configs only C# can build (decode already rejects
/// them). Every §6.4 rule is covered by ConfigGoldenTests.</summary>
public class ControllerConfigValidatorTests
{
    [Fact]
    public void TooManySliders_IsInvalidSliderPinWithNoItem()
    {
        var c = new ControllerConfig();
        for (var i = 0; i < 17; i++) c.Sliders.Add(new Slider { Pin = 33 });
        Assert.Equal(new ValidationResult(ConfigResultCode.Invalid, ValidationResult.NoItem, (byte)ConfigField.SliderPin),
            ControllerConfigValidator.Validate(c, ChipInfo.Esp32));
    }

    [Fact]
    public void TooManyButtons_IsInvalidButtonPinWithNoItem()
    {
        var c = new ControllerConfig();
        for (var i = 0; i < 33; i++) c.Buttons.Add(new Button { Pin = 4 });
        Assert.Equal(new ValidationResult(ConfigResultCode.Invalid, ValidationResult.NoItem, (byte)ConfigField.ButtonPin),
            ControllerConfigValidator.Validate(c, ChipInfo.Esp32));
    }

    [Fact]
    public void EmptyConfig_IsValid()
        => Assert.True(ControllerConfigValidator.Validate(new ControllerConfig(), ChipInfo.Esp32).IsOk);
}
