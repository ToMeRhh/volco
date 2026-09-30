using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Input;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.Core.Tests.Input;

public class SliderProcessorTests
{
    // ── SliderLevelChanged integration test ──────────────────────────────────

    [Fact]
    public async Task SliderLevelChanged_FiresWithCorrectIndexAndLevelInRange()
    {
        // Arrange: build a minimal config with one Device slider at index 0.
        var configManager = NewConfigManager();
        configManager.Config.Sliders.Clear();
        configManager.Config.Sliders.Add(new SliderConfig
        {
            Index = 0,
            Type = SliderMappingType.Device,
            Device = null,
            Invert = false,
            Curve = ResponseCurve.Linear,
            DeadZonePercent = 0,
            SmoothingFactor = 0
        });

        using var audio = new FakeAudioEngine();

        SliderLevelEventArgs? receivedArgs = null;
        var tcs = new TaskCompletionSource<SliderLevelEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var processor = new SliderProcessor(audio, configManager);
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
    }

    [Fact]
    public async Task DeviceSlider_IssuesExactlyOneEngineCallPerTick()
    {
        var configManager = NewConfigManager();
        configManager.Config.Sliders.Clear();
        configManager.Config.Sliders.Add(new SliderConfig
        {
            Index = 0,
            Type = SliderMappingType.Device,
            Device = null,                     // null => current default output
        });

        using var audio = new FakeAudioEngine();
        using var processor = new SliderProcessor(audio, configManager);

        processor.HandleSliderEvent(sliderIndex: 0, adcValue: 2048);
        await WaitForAsync(() => audio.DeviceVolumeCalls.Count >= 1);

        var (device, level) = Assert.Single(audio.DeviceVolumeCalls);
        Assert.Equal(DeviceRefKind.Default, device.Kind);
        Assert.InRange(level, 0.4f, 0.6f);
    }

    [Fact]
    public async Task ProcessSlider_SendsByImageNamesFilter()
    {
        var configManager = NewConfigManager();
        configManager.Config.Sliders.Clear();
        configManager.Config.Sliders.Add(new SliderConfig
        {
            Index = 0,
            Type = SliderMappingType.Processes,
            Processes = new List<string> { "spotify.exe" },
        });

        using var audio = new FakeAudioEngine();
        using var processor = new SliderProcessor(audio, configManager);

        processor.HandleSliderEvent(0, 4095);
        await WaitForAsync(() => audio.SessionVolumeCalls.Count >= 1);

        var (filter, level) = Assert.Single(audio.SessionVolumeCalls);
        var byNames = Assert.IsType<SessionFilter.ByImageNames>(filter);
        Assert.Equal(new[] { "spotify.exe" }, byNames.Names);
        Assert.Equal(1f, level, 2);
    }

    [Fact]
    public async Task UnmappedSlider_SendsUnmappedFilterCarryingMappedNames()
    {
        var configManager = NewConfigManager();
        configManager.Config.Sliders.Clear();
        configManager.Config.Sliders.Add(new SliderConfig
        {
            Index = 0, Type = SliderMappingType.Processes,
            Processes = new List<string> { "spotify.exe" },
        });
        configManager.Config.Sliders.Add(new SliderConfig { Index = 1, Type = SliderMappingType.Unmapped });

        using var audio = new FakeAudioEngine();
        using var processor = new SliderProcessor(audio, configManager);

        processor.HandleSliderEvent(1, 1024);
        await WaitForAsync(() => audio.SessionVolumeCalls.Any(c => c.Filter is SessionFilter.Unmapped));

        var call = audio.SessionVolumeCalls.First(c => c.Filter is SessionFilter.Unmapped);
        var unmapped = Assert.IsType<SessionFilter.Unmapped>(call.Filter);
        Assert.Contains("spotify.exe", unmapped.MappedNames);
    }

    [Fact]
    public async Task NewSessionForMappedProcess_ImmediatelyAppliesThatSlidersLevel()
    {
        var configManager = NewConfigManager();
        configManager.Config.Sliders.Clear();
        configManager.Config.Sliders.Add(new SliderConfig
        {
            Index = 0, Type = SliderMappingType.Processes,
            Processes = new List<string> { "spotify.exe" },
        });

        using var audio = new FakeAudioEngine();
        using var processor = new SliderProcessor(audio, configManager);

        // Establish a level, then let the coalesce tick flush it.
        processor.HandleSliderEvent(0, 1024);
        await WaitForAsync(() => audio.SessionVolumeCalls.Count >= 1);
        var before = audio.SessionVolumeCalls.Count;

        audio.RaiseSessionCreated(new AudioSessionInfo("inst-9", 7, "spotify.exe", "dev-1", false));
        await WaitForAsync(() => audio.SessionVolumeCalls.Count > before);

        var last = audio.SessionVolumeCalls[^1];
        var byNames = Assert.IsType<SessionFilter.ByImageNames>(last.Filter);
        Assert.Contains("spotify.exe", byNames.Names);
        Assert.InRange(last.Level, 0.2f, 0.3f);
    }

    [Fact]
    public async Task NewSessionForClaimedProcessWithNoLevelYet_DoesNotFallThroughToUnmappedSlider()
    {
        var configManager = NewConfigManager();
        configManager.Config.Sliders.Clear();
        // Claims spotify.exe but is never moved -> no level yet.
        configManager.Config.Sliders.Add(new SliderConfig
        {
            Index = 0, Type = SliderMappingType.Processes,
            Processes = new List<string> { "spotify.exe" },
        });
        // Has been moved -> does have a level.
        configManager.Config.Sliders.Add(new SliderConfig { Index = 1, Type = SliderMappingType.Unmapped });

        using var audio = new FakeAudioEngine();
        using var processor = new SliderProcessor(audio, configManager);

        processor.HandleSliderEvent(1, 1024);
        await WaitForAsync(() => audio.SessionVolumeCalls.Count >= 1);
        var before = audio.SessionVolumeCalls.Count;

        // FakeAudioEngine raises this synchronously, so OnSessionsChanged has already run
        // by the time this call returns — no polling needed.
        audio.RaiseSessionCreated(new AudioSessionInfo("inst-1", 5, "spotify.exe", "dev-1", false));

        Assert.Equal(before, audio.SessionVolumeCalls.Count);
        Assert.DoesNotContain(audio.SessionVolumeCalls, c =>
            c.Filter is SessionFilter.ByImageNames byNames && byNames.Names.Contains("spotify.exe"));
    }

    // ── Fix B: OSD must not show for a Device slider whose target doesn't resolve ─────────────

    [Fact]
    public async Task DeviceSlider_AbsentDevice_RaisesSliderLevelButNotVolumeChanged()
    {
        var configManager = NewConfigManager();
        configManager.Config.Sliders.Clear();
        configManager.Config.Sliders.Add(new SliderConfig
        {
            Index = 0,
            Type = SliderMappingType.Device,
            Device = new DeviceRefConfig { Id = "absent-dev", FriendlyName = "Unplugged Headset" },
        });

        // FakeAudioEngine.DeviceStates has no entry for "absent-dev" -> TryGetDeviceState false.
        using var audio = new FakeAudioEngine();
        using var processor = new SliderProcessor(audio, configManager);

        var levelTcs = new TaskCompletionSource<SliderLevelEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        processor.SliderLevelChanged += (_, e) => levelTcs.TrySetResult(e);
        var sawVolumeChanged = false;
        processor.VolumeChanged += (_, _) => sawVolumeChanged = true;

        processor.HandleSliderEvent(0, 2048);

        var completed = await Task.WhenAny(levelTcs.Task, Task.Delay(500));
        Assert.True(completed == levelTcs.Task, "SliderLevelChanged did not fire");

        // Give a wrongly-raised VolumeChanged (same coalesce tick) a chance to show up.
        await Task.Delay(100);
        Assert.False(sawVolumeChanged, "VolumeChanged fired for a slider mapped to an absent device");
    }

    [Fact]
    public async Task DeviceSlider_PresentDevice_RaisesBothSliderLevelAndVolumeChanged()
    {
        var configManager = NewConfigManager();
        configManager.Config.Sliders.Clear();
        configManager.Config.Sliders.Add(new SliderConfig
        {
            Index = 0,
            Type = SliderMappingType.Device,
            Device = new DeviceRefConfig { Id = "present-dev", FriendlyName = "Headphones" },
        });

        using var audio = new FakeAudioEngine();
        audio.DeviceStates["present-dev"] = (0.5f, false);
        using var processor = new SliderProcessor(audio, configManager);

        var levelTcs = new TaskCompletionSource<SliderLevelEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var volumeTcs = new TaskCompletionSource<VolumeChangeNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        processor.SliderLevelChanged += (_, e) => levelTcs.TrySetResult(e);
        processor.VolumeChanged += (_, e) => volumeTcs.TrySetResult(e);

        processor.HandleSliderEvent(0, 2048);

        var completedLevel = await Task.WhenAny(levelTcs.Task, Task.Delay(500));
        Assert.True(completedLevel == levelTcs.Task, "SliderLevelChanged did not fire");
        var completedVolume = await Task.WhenAny(volumeTcs.Task, Task.Delay(500));
        Assert.True(completedVolume == volumeTcs.Task, "VolumeChanged did not fire for a slider mapped to a present device");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static ConfigManager NewConfigManager()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"volco-slidertest-{Guid.NewGuid()}", "config.json");
        var cm = new ConfigManager(tempPath);
        cm.Load();
        return cm;
    }

    /// <summary>Polls until <paramref name="condition"/> holds or the timeout expires.</summary>
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 1000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail($"Condition not met within {timeoutMs}ms");
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
        var slider = new SliderConfig { Type = SliderMappingType.Device, Device = null };
        Assert.Equal("Speakers (Realtek)", SliderProcessor.BuildSliderLabel(slider, "Speakers (Realtek)"));
    }

    [Fact]
    public void BuildSliderLabel_DeviceWithEmptyNameAndNoCurrentOutput_FallsBack()
    {
        var slider = new SliderConfig { Type = SliderMappingType.Device, Device = null };
        Assert.Equal("Output", SliderProcessor.BuildSliderLabel(slider, null));
    }

    [Fact]
    public void BuildSliderLabel_DeviceWithName_UsesDeviceName()
    {
        var slider = new SliderConfig { Type = SliderMappingType.Device, Device = new DeviceRefConfig { FriendlyName = "Headphones" } };
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
