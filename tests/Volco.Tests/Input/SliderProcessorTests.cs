using Volco.Audio;
using Volco.Config;
using Volco.Input;
using Xunit;

namespace Volco.Tests.Input;

public class SliderProcessorTests
{
    [Fact]
    public void MapAdcToVolume_ZeroReturnsZero()
    {
        Assert.Equal(0f, SliderProcessor.MapAdcToVolume(0, invert: false), 3);
    }

    [Fact]
    public void MapAdcToVolume_MaxReturnsOne()
    {
        Assert.Equal(1f, SliderProcessor.MapAdcToVolume(4095, invert: false), 3);
    }

    [Fact]
    public void MapAdcToVolume_MidReturnsHalf()
    {
        Assert.Equal(0.5f, SliderProcessor.MapAdcToVolume(2048, invert: false), 1);
    }

    [Fact]
    public void MapAdcToVolume_InvertFlipsValue()
    {
        var normal = SliderProcessor.MapAdcToVolume(200, invert: false);
        var inverted = SliderProcessor.MapAdcToVolume(200, invert: true);
        Assert.Equal(1f, normal + inverted, 3);
    }

    [Fact]
    public void GetUnmappedProcesses_ExcludesMappedProcesses()
    {
        var config = new AppConfig();
        config.Sliders.Add(new SliderConfig
        {
            Index = 0,
            Type = SliderMappingType.Processes,
            Processes = new List<string> { "chrome.exe", "spotify.exe" }
        });
        config.Sliders.Add(new SliderConfig
        {
            Index = 1,
            Type = SliderMappingType.Unmapped
        });

        var activeProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chrome.exe", "spotify.exe", "discord.exe", "firefox.exe"
        };

        var unmapped = SliderProcessor.GetUnmappedProcesses(config, activeProcesses);

        Assert.Contains("discord.exe", unmapped);
        Assert.Contains("firefox.exe", unmapped);
        Assert.DoesNotContain("chrome.exe", unmapped);
        Assert.DoesNotContain("spotify.exe", unmapped);
    }

    [Fact]
    public void BuildSliderLabel_DeviceWithEmptyName_UsesCurrentOutput()
    {
        var slider = new SliderConfig { Type = SliderMappingType.Device, DeviceName = "" };
        Assert.Equal("Speakers (Realtek)", SliderProcessor.BuildSliderLabel(slider, "Speakers (Realtek)"));
    }

    [Fact]
    public void BuildSliderLabel_DeviceWithEmptyNameAndNoCurrentOutput_FallsBack()
    {
        var slider = new SliderConfig { Type = SliderMappingType.Device, DeviceName = "" };
        Assert.Equal("Output", SliderProcessor.BuildSliderLabel(slider, null));
    }

    [Fact]
    public void BuildSliderLabel_DeviceWithName_UsesDeviceName()
    {
        var slider = new SliderConfig { Type = SliderMappingType.Device, DeviceName = "Headphones" };
        Assert.Equal("Headphones", SliderProcessor.BuildSliderLabel(slider, "Speakers"));
    }

    [Fact]
    public void BuildSliderLabel_SingleProcess_StripsExeAndCapitalizes()
    {
        var slider = new SliderConfig
        {
            Type = SliderMappingType.Processes,
            Processes = new List<string> { "spotify.exe" }
        };
        Assert.Equal("Spotify", SliderProcessor.BuildSliderLabel(slider, null));
    }

    [Fact]
    public void BuildSliderLabel_MultipleProcesses_JoinsWithComma()
    {
        var slider = new SliderConfig
        {
            Type = SliderMappingType.Processes,
            Processes = new List<string> { "chrome.exe", "spotify.exe" }
        };
        Assert.Equal("Chrome, Spotify", SliderProcessor.BuildSliderLabel(slider, null));
    }

    [Fact]
    public void BuildSliderLabel_EmptyProcessList_UsesAppsFallback()
    {
        var slider = new SliderConfig { Type = SliderMappingType.Processes, Processes = new List<string>() };
        Assert.Equal("Apps", SliderProcessor.BuildSliderLabel(slider, null));
    }

    [Fact]
    public void BuildSliderLabel_Unmapped_UsesOtherApps()
    {
        var slider = new SliderConfig { Type = SliderMappingType.Unmapped };
        Assert.Equal("Other apps", SliderProcessor.BuildSliderLabel(slider, null));
    }

    [Fact]
    public void MapAdcToVolume_DeadZone_ClampsLowEndToZero()
    {
        // 10% dead zone: anything below 10% of range maps to 0
        var v = SliderProcessor.MapAdcToVolume(200, invert: false, ResponseCurve.Linear, 0.1);
        Assert.Equal(0f, v, 3);
    }

    [Fact]
    public void MapAdcToVolume_DeadZone_RescalesAboveThreshold()
    {
        // just above dead zone -> just above 0; full stays 1
        Assert.Equal(1f, SliderProcessor.MapAdcToVolume(4095, false, ResponseCurve.Linear, 0.1), 3);
        Assert.True(SliderProcessor.MapAdcToVolume(2048, false, ResponseCurve.Linear, 0.1) < 0.5f);
    }

    [Fact]
    public void MapAdcToVolume_Logarithmic_BoostsLowEnd()
    {
        var lin = SliderProcessor.MapAdcToVolume(2048, false, ResponseCurve.Linear, 0);
        var log = SliderProcessor.MapAdcToVolume(2048, false, ResponseCurve.Logarithmic, 0);
        Assert.True(log > lin);
    }

    [Fact]
    public void MapAdcToVolume_Curves_PreserveEndpoints()
    {
        foreach (var c in Enum.GetValues<ResponseCurve>())
        {
            Assert.Equal(0f, SliderProcessor.MapAdcToVolume(0, false, c, 0), 3);
            Assert.Equal(1f, SliderProcessor.MapAdcToVolume(4095, false, c, 0), 3);
        }
    }
}
