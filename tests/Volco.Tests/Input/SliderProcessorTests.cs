using Volco.Audio;
using Volco.Config;
using Volco.Input;
using Xunit;

namespace Volco.Tests.Input;

public class SliderProcessorTests
{
    // ── SliderLevelChanged integration test ──────────────────────────────────

    [Fact]
    public async Task SliderLevelChanged_FiresWithCorrectIndexAndLevelInRange()
    {
        // Arrange: build a minimal config with one Device slider at index 0.
        var tempPath = Path.Combine(Path.GetTempPath(), $"volco-slidertest-{Guid.NewGuid()}", "config.json");
        var configManager = new ConfigManager(tempPath);
        configManager.Load();
        configManager.Config.Sliders.Clear();
        configManager.Config.Sliders.Add(new SliderConfig
        {
            Index = 0,
            Type = SliderMappingType.Device,
            DeviceName = "",
            Invert = false,
            Curve = ResponseCurve.Linear,
            DeadZonePercent = 0,
            SmoothingFactor = 0
        });

        var audio = new NoOpAudioManager();
        var sessionMonitor = new SessionMonitor(audio, pollIntervalMs: 60000); // no-op poll

        SliderLevelEventArgs? receivedArgs = null;
        var tcs = new TaskCompletionSource<SliderLevelEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var processor = new SliderProcessor(audio, configManager, sessionMonitor);
        processor.SliderLevelChanged += (_, e) =>
        {
            if (!tcs.Task.IsCompleted)
                tcs.TrySetResult(e);
        };

        // Act: send mid-range ADC value (2048 ≈ 0.5 volume).
        processor.HandleSliderEvent(sliderIndex: 0, adcValue: 2048);

        // Wait up to 500ms for the 20ms coalesce timer to fire.
        var timeout = Task.Delay(500);
        var completed = await Task.WhenAny(tcs.Task, timeout);

        // Assert
        Assert.True(completed == tcs.Task, "SliderLevelChanged did not fire within 500ms");
        receivedArgs = await tcs.Task;
        Assert.Equal(0, receivedArgs.SliderIndex);
        Assert.InRange(receivedArgs.Level, 0f, 1f);

        sessionMonitor.Dispose();
    }

    /// <summary>Minimal IAudioManager that does nothing — avoids any Windows audio calls.</summary>
    private sealed class NoOpAudioManager : IAudioManager
    {
        public void SetProcessVolume(string processName, float volume) { }
        public void SetProcessVolumes(IReadOnlyCollection<string> processNames, float volume) { }
        public void SetDeviceVolume(float volume, string? deviceName = null) { }
        public void ToggleMuteOutput(string? deviceId = null) { }
        public void ToggleMuteInput() { }
        public void SetDefaultOutputDevice(string deviceName) { }
        public string? GetDefaultOutputDeviceName() => null;
        public List<AudioDeviceInfo> GetOutputDevices() => new();
        public List<AudioDeviceInfo> GetInputDevices() => new();
        public List<string> GetActiveProcessNames() => new();
        public bool IsOutputMuted(string? deviceId = null) => false;
        public bool IsInputMuted() => false;
    }


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

    [Fact]
    public void ApplySmoothing_Zero_ReturnsTarget()
        => Assert.Equal(0.8f, SliderProcessor.ApplySmoothing(0.2f, 0.8f, 0), 3);

    [Fact]
    public void ApplySmoothing_MovesPartwayTowardTarget()
    {
        var r = SliderProcessor.ApplySmoothing(0f, 1f, 0.5);
        Assert.True(r > 0f && r < 1f);
    }
}
