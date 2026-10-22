using Volco.Core.Audio;
using Volco.Core.Communication;
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
            DeadZonePercent = 0
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
        await WaitForAsync(() => audio.Snapshot(audio.DeviceVolumeCalls).Count >= 1);

        var (device, level) = Assert.Single(audio.Snapshot(audio.DeviceVolumeCalls));
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
        await WaitForAsync(() => audio.Snapshot(audio.SessionVolumeCalls).Count >= 1);

        var (filter, level) = Assert.Single(audio.Snapshot(audio.SessionVolumeCalls));
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
        await WaitForAsync(() => audio.Snapshot(audio.SessionVolumeCalls).Any(c => c.Filter is SessionFilter.Unmapped));

        var call = audio.Snapshot(audio.SessionVolumeCalls).First(c => c.Filter is SessionFilter.Unmapped);
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
        await WaitForAsync(() => audio.Snapshot(audio.SessionVolumeCalls).Count >= 1);
        var before = audio.Snapshot(audio.SessionVolumeCalls).Count;

        audio.RaiseSessionCreated(new AudioSessionInfo("inst-9", 7, "spotify.exe", "dev-1", false));
        await WaitForAsync(() => audio.Snapshot(audio.SessionVolumeCalls).Count > before);

        var last = audio.Snapshot(audio.SessionVolumeCalls)[^1];
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
        await WaitForAsync(() => audio.Snapshot(audio.SessionVolumeCalls).Count >= 1);
        var before = audio.Snapshot(audio.SessionVolumeCalls).Count;

        // FakeAudioEngine raises this synchronously, so OnSessionsChanged has already run
        // by the time this call returns — no polling needed.
        audio.RaiseSessionCreated(new AudioSessionInfo("inst-1", 5, "spotify.exe", "dev-1", false));

        Assert.Equal(before, audio.Snapshot(audio.SessionVolumeCalls).Count);
        Assert.DoesNotContain(audio.Snapshot(audio.SessionVolumeCalls), c =>
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
        // Written on the coalescing-timer thread, read on this (test) thread after the awaits
        // below - plain bool read/write isn't guaranteed to be visible across threads without a
        // memory barrier, so use Interlocked/Volatile rather than a bare bool.
        int volumeChangedFired = 0;
        processor.VolumeChanged += (_, _) => Interlocked.Exchange(ref volumeChangedFired, 1);

        processor.HandleSliderEvent(0, 2048);

        var completed = await Task.WhenAny(levelTcs.Task, Task.Delay(500));
        Assert.True(completed == levelTcs.Task, "SliderLevelChanged did not fire");

        // Give a wrongly-raised VolumeChanged (same coalesce tick) a chance to show up.
        await Task.Delay(100);
        Assert.Equal(0, Volatile.Read(ref volumeChangedFired));
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

    [Fact]
    public async Task VolumeChanged_AppsSlider_CarriesItsApps_DeviceSlider_None()
    {
        var configManager = NewConfigManager();
        configManager.Config.Sliders.Clear();
        configManager.Config.Sliders.Add(new SliderConfig { Index = 0, Type = SliderMappingType.Processes, Processes = { "spotify.exe", "discord.exe" } });
        configManager.Config.Sliders.Add(new SliderConfig
        {
            Index = 1,
            Type = SliderMappingType.Device,
            Device = new DeviceRefConfig { Id = "present-dev", FriendlyName = "Headphones" },
        });

        using var audio = new FakeAudioEngine();
        audio.DeviceStates["present-dev"] = (0.5f, false);
        using var processor = new SliderProcessor(audio, configManager);
        var apps = new TaskCompletionSource<VolumeChangeNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = new TaskCompletionSource<VolumeChangeNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        processor.VolumeChanged += (_, e) => (e.Apps is null ? device : apps).TrySetResult(e);

        processor.HandleSliderEvent(0, 2048);
        processor.HandleSliderEvent(1, 2048);

        Assert.True(await Task.WhenAny(apps.Task, Task.Delay(500)) == apps.Task, "no notification for the apps slider");
        Assert.True(await Task.WhenAny(device.Task, Task.Delay(500)) == device.Task, "no notification for the device slider");
        Assert.Equal(new[] { "spotify.exe", "discord.exe" }, apps.Task.Result.Apps);
    }

    // ── Sync: the firmware reports every slider after each Hello ──────────────────────────

    private static ConfigManager PresentDeviceSliderConfig()
    {
        var configManager = NewConfigManager();
        configManager.Config.Sliders.Clear();
        configManager.Config.Sliders.Add(new SliderConfig
        {
            Index = 0,
            Type = SliderMappingType.Device,
            Device = new DeviceRefConfig { Id = "present-dev", FriendlyName = "Headphones" },
        });
        return configManager;
    }

    [Fact]
    public async Task SyncValue_SetsVolumeAndLevel_WithoutOsd()
    {
        using var audio = new FakeAudioEngine();
        audio.DeviceStates["present-dev"] = (0.5f, false);
        using var processor = new SliderProcessor(audio, PresentDeviceSliderConfig());

        var levelTcs = new TaskCompletionSource<SliderLevelEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        processor.SliderLevelChanged += (_, e) => levelTcs.TrySetResult(e);
        int volumeChangedFired = 0;
        processor.VolumeChanged += (_, _) => Interlocked.Exchange(ref volumeChangedFired, 1);

        processor.BeginSync();
        processor.HandleSliderEvent(0, 4095);

        var completed = await Task.WhenAny(levelTcs.Task, Task.Delay(500));
        Assert.True(completed == levelTcs.Task, "SliderLevelChanged did not fire for a sync value");
        var (_, level) = Assert.Single(audio.Snapshot(audio.DeviceVolumeCalls));
        Assert.Equal(1f, level, 2);
        Assert.Equal(1f, processor.GetLastLevel(0)!.Value, 2);

        await Task.Delay(100);
        Assert.Equal(0, Volatile.Read(ref volumeChangedFired));
    }

    [Fact]
    public async Task ValueAfterTheSyncValue_ShowsOsd()
    {
        using var audio = new FakeAudioEngine();
        audio.DeviceStates["present-dev"] = (0.5f, false);
        using var processor = new SliderProcessor(audio, PresentDeviceSliderConfig());

        var volumeTcs = new TaskCompletionSource<VolumeChangeNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        processor.VolumeChanged += (_, e) => volumeTcs.TrySetResult(e);

        processor.BeginSync();
        processor.HandleSliderEvent(0, 1000);
        await WaitForAsync(() => audio.Snapshot(audio.DeviceVolumeCalls).Count >= 1);
        Assert.False(volumeTcs.Task.IsCompleted);

        processor.HandleSliderEvent(0, 2000);
        var completed = await Task.WhenAny(volumeTcs.Task, Task.Delay(500));
        Assert.True(completed == volumeTcs.Task, "VolumeChanged did not fire for a movement after the sync value");
    }

    [Fact]
    public async Task SyncExpires_FirstValueAfterTheWindowShowsOsd()
    {
        using var audio = new FakeAudioEngine();
        audio.DeviceStates["present-dev"] = (0.5f, false);
        using var processor = new SliderProcessor(audio, PresentDeviceSliderConfig(), syncWindow: TimeSpan.FromMilliseconds(50));

        var volumeTcs = new TaskCompletionSource<VolumeChangeNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        processor.VolumeChanged += (_, e) => volumeTcs.TrySetResult(e);

        // The sync frame was lost (UDP): a real movement later must not be silenced.
        processor.BeginSync();
        await Task.Delay(150);
        processor.HandleSliderEvent(0, 2048);

        var completed = await Task.WhenAny(volumeTcs.Task, Task.Delay(500));
        Assert.True(completed == volumeTcs.Task, "VolumeChanged did not fire after the sync window expired");
    }

    // ── Issue #48: the slider OSD carries the governing device's mute flag ─────────────────

    [Fact]
    public async Task DeviceSlider_MutedDevice_RaisesVolumeChangedWithMutedTrue()
    {
        var configManager = NewConfigManager();
        configManager.Config.Sliders.Clear();
        configManager.Config.Sliders.Add(new SliderConfig
        {
            Index = 0,
            Type = SliderMappingType.Device,
            Device = new DeviceRefConfig { Id = "muted-dev", FriendlyName = "Headphones" },
        });

        using var audio = new FakeAudioEngine();
        audio.DeviceStates["muted-dev"] = (0.5f, true);
        using var processor = new SliderProcessor(audio, configManager);

        var volumeTcs = new TaskCompletionSource<VolumeChangeNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        processor.VolumeChanged += (_, e) => volumeTcs.TrySetResult(e);

        processor.HandleSliderEvent(0, 2048);

        var completedVolume = await Task.WhenAny(volumeTcs.Task, Task.Delay(500));
        Assert.True(completedVolume == volumeTcs.Task, "VolumeChanged did not fire for a muted device slider");
        var notification = await volumeTcs.Task;
        Assert.True(notification.Muted, "Notification should have Muted = true for a muted device");
        // Moving the slider never unmutes the device.
        Assert.Empty(audio.Snapshot(audio.SetMuteCalls));
        Assert.Empty(audio.Snapshot(audio.ToggleMuteCalls));
    }

    [Fact]
    public async Task DeviceSlider_DefaultOutputMuted_RaisesMutedTrue()
    {
        using var audio = EngineWithDefaultOutput(defaultMuted: true);
        var n = await MoveSliderAndCaptureOsd(audio, new SliderConfig { Index = 0, Type = SliderMappingType.Device, Device = null });
        Assert.True(n.Muted);
    }

    [Fact]
    public async Task DeviceSlider_DefaultOutputUnmuted_RaisesMutedFalse()
    {
        using var audio = EngineWithDefaultOutput(defaultMuted: false);
        var n = await MoveSliderAndCaptureOsd(audio, new SliderConfig { Index = 0, Type = SliderMappingType.Device, Device = null });
        Assert.False(n.Muted);
    }

    [Fact]
    public async Task DeviceSlider_OwnDeviceUnmuted_IgnoresMutedDefault()
    {
        using var audio = EngineWithDefaultOutput(defaultMuted: true);
        var n = await MoveSliderAndCaptureOsd(audio, new SliderConfig
        {
            Index = 0,
            Type = SliderMappingType.Device,
            Device = new DeviceRefConfig { Id = "hp", FriendlyName = "Headphones" },
        });
        Assert.False(n.Muted);
    }

    [Fact]
    public async Task ProcessesSlider_DefaultOutputMuted_RaisesMutedTrue()
    {
        using var audio = EngineWithDefaultOutput(defaultMuted: true);
        var n = await MoveSliderAndCaptureOsd(audio, new SliderConfig { Index = 0, Type = SliderMappingType.Processes, Processes = { "spotify.exe" } });
        Assert.True(n.Muted);
    }

    [Fact]
    public async Task ProcessesSlider_DefaultOutputUnmuted_RaisesMutedFalse()
    {
        using var audio = EngineWithDefaultOutput(defaultMuted: false);
        var n = await MoveSliderAndCaptureOsd(audio, new SliderConfig { Index = 0, Type = SliderMappingType.Processes, Processes = { "spotify.exe" } });
        Assert.False(n.Muted);
    }

    [Fact]
    public async Task UnmappedSlider_DefaultOutputMuted_RaisesMutedTrue()
    {
        using var audio = EngineWithDefaultOutput(defaultMuted: true);
        var n = await MoveSliderAndCaptureOsd(audio, new SliderConfig { Index = 0, Type = SliderMappingType.Unmapped });
        Assert.True(n.Muted);
    }

    // ── The slider OSD's icon: the slider's own device, else the current output ──────────────

    [Fact]
    public async Task DeviceSlider_CarriesItsOwnDevicesIcon()
    {
        using var audio = EngineWithIconDevices();
        var n = await MoveSliderAndCaptureOsd(audio, new SliderConfig
        {
            Index = 0,
            Type = SliderMappingType.Device,
            Device = new DeviceRefConfig { Id = "hp", FriendlyName = "Headphones (USB)" },
        });
        Assert.Equal(ScreenIcon.Headphones, n.Icon);
        Assert.Equal("Headphones", n.Label);
    }

    [Fact]
    public async Task DeviceSlider_CarriesTheChosenIcon_OverTheFormFactor()
    {
        using var audio = EngineWithIconDevices();
        var n = await MoveSliderAndCaptureOsd(audio, new SliderConfig
        {
            Index = 0,
            Type = SliderMappingType.Device,
            Device = new DeviceRefConfig { Id = "hp", FriendlyName = "Headphones (USB)" },
        }, config => config.Devices.Add(new DeviceSettingsConfig { Id = "hp", Nickname = "Cans", Icon = ScreenIcon.Earbuds }));
        Assert.Equal(ScreenIcon.Earbuds, n.Icon);
        Assert.Equal("Cans", n.Label);
    }

    private static FakeAudioEngine EngineWithMic()
    {
        var audio = EngineWithIconDevices();
        audio.Devices.Add(new AudioDevice("mic", "Microphone (Blue Yeti)", AudioFlow.Capture, true, DeviceFormFactor.Microphone));
        audio.DeviceStates["mic"] = (0.5f, false);
        return audio;
    }

    private static readonly SliderConfig MicSlider = new()
    {
        Index = 0,
        Type = SliderMappingType.Device,
        Device = new DeviceRefConfig { Id = "mic", FriendlyName = "Microphone (Blue Yeti)" },
    };

    [Fact]
    public async Task CaptureDeviceSlider_SetsThatDeviceVolume()
    {
        using var audio = EngineWithMic();
        await MoveSliderAndCaptureOsd(audio, MicSlider);

        var (device, _) = Assert.Single(audio.Snapshot(audio.DeviceVolumeCalls));
        Assert.Equal(DeviceRefKind.ById, device.Kind);
        Assert.Equal("mic", device.Id);
    }

    [Fact]
    public async Task CaptureDeviceSlider_OsdIconIsMic()
    {
        using var audio = EngineWithMic();
        var n = await MoveSliderAndCaptureOsd(audio, MicSlider);
        Assert.Equal(ScreenIcon.Mic, n.Icon);
        Assert.Equal("Microphone", n.Label);
    }

    [Fact]
    public async Task CaptureDeviceSlider_UnknownFormFactor_OsdIconIsMic()
    {
        using var audio = EngineWithIconDevices();
        audio.Devices.Add(new AudioDevice("line", "Line In (Realtek)", AudioFlow.Capture, true));
        audio.DeviceStates["line"] = (0.5f, false);
        var n = await MoveSliderAndCaptureOsd(audio, new SliderConfig
        {
            Index = 0,
            Type = SliderMappingType.Device,
            Device = new DeviceRefConfig { Id = "line", FriendlyName = "Line In (Realtek)" },
        });
        Assert.Equal(ScreenIcon.Mic, n.Icon);
    }

    [Fact]
    public async Task DeviceSlider_ForTheCurrentOutput_CarriesTheCurrentOutputsIcon()
    {
        using var audio = EngineWithIconDevices();
        var n = await MoveSliderAndCaptureOsd(audio, new SliderConfig { Index = 0, Type = SliderMappingType.Device, Device = null });
        Assert.Equal(ScreenIcon.Speaker, n.Icon);
        Assert.Equal("Speakers", n.Label);
    }

    [Fact]
    public async Task ProcessesSlider_CarriesTheCurrentOutputsIcon()
    {
        using var audio = EngineWithIconDevices();
        var n = await MoveSliderAndCaptureOsd(audio, new SliderConfig { Index = 0, Type = SliderMappingType.Processes, Processes = { "spotify.exe" } });
        Assert.Equal(ScreenIcon.Speaker, n.Icon);
        Assert.Equal("Spotify", n.Label);
    }

    [Fact]
    public async Task UnmappedSlider_CarriesTheCurrentOutputsIcon()
    {
        using var audio = EngineWithIconDevices();
        var n = await MoveSliderAndCaptureOsd(audio, new SliderConfig { Index = 0, Type = SliderMappingType.Unmapped });
        Assert.Equal(ScreenIcon.Speaker, n.Icon);
    }

    [Fact]
    public async Task ProcessesSlider_WithoutACurrentOutput_CarriesNoIcon()
    {
        using var audio = new FakeAudioEngine();
        var n = await MoveSliderAndCaptureOsd(audio, new SliderConfig { Index = 0, Type = SliderMappingType.Processes, Processes = { "spotify.exe" } });
        Assert.Null(n.Icon);
    }

    // ── Fix D: GetLastLevel ──────────────────────────────────────────────────

    [Fact]
    public void GetLastLevel_NullBeforeAnyEvent()
    {
        var configManager = NewConfigManager();
        using var audio = new FakeAudioEngine();
        using var processor = new SliderProcessor(audio, configManager);

        Assert.Null(processor.GetLastLevel(0));
    }

    [Fact]
    public void GetLastLevel_OutOfRangeIndex_ReturnsNull()
    {
        var configManager = NewConfigManager();
        using var audio = new FakeAudioEngine();
        using var processor = new SliderProcessor(audio, configManager);

        Assert.Null(processor.GetLastLevel(-1));
        Assert.Null(processor.GetLastLevel(999));
    }

    [Fact]
    public async Task GetLastLevel_EqualsProcessedVolume_AfterOneEvent()
    {
        var configManager = NewConfigManager();
        configManager.Config.Sliders.Clear();
        configManager.Config.Sliders.Add(new SliderConfig { Index = 0, Type = SliderMappingType.Unmapped });

        using var audio = new FakeAudioEngine();
        using var processor = new SliderProcessor(audio, configManager);

        var tcs = new TaskCompletionSource<SliderLevelEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        processor.SliderLevelChanged += (_, e) => tcs.TrySetResult(e);

        processor.HandleSliderEvent(0, 2048);
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(500));
        Assert.True(completed == tcs.Task, "SliderLevelChanged did not fire");
        var received = await tcs.Task;

        Assert.Equal(received.Level, processor.GetLastLevel(0));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static FakeAudioEngine EngineWithDefaultOutput(bool defaultMuted)
    {
        var audio = new FakeAudioEngine();
        audio.DefaultRender = new AudioDevice("spk", "Speakers", AudioFlow.Render, true);
        audio.DeviceStates["spk"] = (0.5f, defaultMuted);
        audio.DeviceStates["hp"] = (0.5f, false);
        return audio;
    }

    /// <summary>Speakers (the default output) and headphones, each with its form factor.</summary>
    private static FakeAudioEngine EngineWithIconDevices()
    {
        var audio = new FakeAudioEngine();
        var spk = new AudioDevice("spk", "Speakers (Realtek)", AudioFlow.Render, true, DeviceFormFactor.Speakers);
        var hp = new AudioDevice("hp", "Headphones (USB)", AudioFlow.Render, false, DeviceFormFactor.Headphones);
        audio.Devices = new List<AudioDevice> { spk, hp };
        audio.DefaultRender = spk;
        audio.DeviceStates["spk"] = (0.5f, false);
        audio.DeviceStates["hp"] = (0.5f, false);
        return audio;
    }

    private static async Task<VolumeChangeNotification> MoveSliderAndCaptureOsd(
        FakeAudioEngine audio, SliderConfig slider, Action<AppConfig>? configure = null)
    {
        var configManager = NewConfigManager();
        configManager.Config.Sliders.Clear();
        configManager.Config.Sliders.Add(slider);
        configure?.Invoke(configManager.Config);
        using var processor = new SliderProcessor(audio, configManager);

        var tcs = new TaskCompletionSource<VolumeChangeNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        processor.VolumeChanged += (_, e) => tcs.TrySetResult(e);
        processor.HandleSliderEvent(0, 2048);

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(500));
        Assert.True(completed == tcs.Task, "VolumeChanged did not fire");
        return await tcs.Task;
    }

    private static ConfigManager NewConfigManager()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"volco-slidertest-{Guid.NewGuid()}", "config.json");
        var cm = new ConfigManager(tempPath);
        cm.Load();
        return cm;
    }

    /// <summary>Polls until <paramref name="condition"/> holds or the timeout expires.</summary>
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
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
    public async Task Reapply_SetsTheNewTargetToTheLastPosition_NoOsd()
    {
        var configManager = NewConfigManager();
        configManager.Config.Sliders.Clear();
        configManager.Config.Sliders.Add(new SliderConfig { Index = 0, Type = SliderMappingType.Device });
        using var audio = new FakeAudioEngine();
        using var processor = new SliderProcessor(audio, configManager);
        processor.HandleSliderEvent(0, 4095);
        await WaitForAsync(() => audio.Snapshot(audio.DeviceVolumeCalls).Count >= 1);
        await Task.Delay(100);   // the first value's own OSD is raised right after its write
        var osd = 0;
        processor.VolumeChanged += (_, _) => Interlocked.Increment(ref osd);

        configManager.Config.Sliders[0].Type = SliderMappingType.Processes;
        configManager.Config.Sliders[0].Processes = new List<string> { "spotify.exe" };
        processor.Reapply(0);
        await WaitForAsync(() => audio.Snapshot(audio.SessionVolumeCalls).Count >= 1);

        var (filter, level) = Assert.Single(audio.Snapshot(audio.SessionVolumeCalls));
        Assert.Equal(new[] { "spotify.exe" }, Assert.IsType<SessionFilter.ByImageNames>(filter).Names);
        Assert.Equal(audio.Snapshot(audio.DeviceVolumeCalls)[0].Level, level, 3);
        Assert.Equal(0, osd);
    }

    [Fact]
    public async Task Reapply_BeforeAnyPosition_DoesNothing()
    {
        var configManager = NewConfigManager();
        using var audio = new FakeAudioEngine();
        using var processor = new SliderProcessor(audio, configManager);

        processor.Reapply(0);
        await Task.Delay(100);

        Assert.Empty(audio.Snapshot(audio.DeviceVolumeCalls));
        Assert.Empty(audio.Snapshot(audio.SessionVolumeCalls));
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
}
