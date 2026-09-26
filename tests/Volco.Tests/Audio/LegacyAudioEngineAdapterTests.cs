using Volco.Audio;
using Xunit;

namespace Volco.Tests.Audio;

public class LegacyAudioEngineAdapterTests
{
    [Fact]
    public void SetDeviceVolume_Default_PassesNullDeviceName()
    {
        var legacy = new RecordingAudioManager();
        using var adapter = new LegacyAudioEngineAdapter(legacy);

        adapter.SetDeviceVolume(DeviceRef.Default(AudioFlow.Render, AudioRole.Multimedia), 0.3f);

        var (name, volume) = Assert.Single(legacy.DeviceVolumeCalls);
        Assert.Null(name);
        Assert.Equal(0.3f, volume, 3);
    }

    [Fact]
    public void SetDeviceVolume_ByFriendlyName_PassesTheName()
    {
        var legacy = new RecordingAudioManager();
        using var adapter = new LegacyAudioEngineAdapter(legacy);

        adapter.SetDeviceVolume(DeviceRef.ByFriendlyName("Headphones"), 0.6f);

        var (name, _) = Assert.Single(legacy.DeviceVolumeCalls);
        Assert.Equal("Headphones", name);
    }

    [Fact]
    public void SetDeviceVolume_ById_FallsBackToDefaultDevice()
    {
        // The legacy manager has no concept of endpoint ids. Rather than pass an id into a
        // name-based Contains() lookup (which would match the wrong device), the adapter
        // treats an id-only ref as "the default device".
        var legacy = new RecordingAudioManager();
        using var adapter = new LegacyAudioEngineAdapter(legacy);

        adapter.SetDeviceVolume(DeviceRef.ById("{0.0.0.00000000}.{abc}"), 0.7f);

        var (name, _) = Assert.Single(legacy.DeviceVolumeCalls);
        Assert.Null(name);
    }

    [Fact]
    public void SetSessionVolume_ByImageNames_ForwardsTheNames()
    {
        var legacy = new RecordingAudioManager();
        using var adapter = new LegacyAudioEngineAdapter(legacy);

        adapter.SetSessionVolume(new SessionFilter.ByImageNames(new[] { "spotify.exe" }), 0.4f);

        var (names, volume) = Assert.Single(legacy.ProcessVolumeCalls);
        Assert.Equal(new[] { "spotify.exe" }, names);
        Assert.Equal(0.4f, volume, 3);
    }

    [Fact]
    public void SetSessionVolume_Unmapped_ResolvesAgainstActiveProcesses()
    {
        var legacy = new RecordingAudioManager
        {
            ActiveProcessNames = new List<string> { "chrome.exe", "spotify.exe", "discord.exe" }
        };
        using var adapter = new LegacyAudioEngineAdapter(legacy);

        adapter.SetSessionVolume(new SessionFilter.Unmapped(new[] { "spotify.exe" }), 0.2f);

        var (names, _) = Assert.Single(legacy.ProcessVolumeCalls);
        Assert.Contains("chrome.exe", names);
        Assert.Contains("discord.exe", names);
        Assert.DoesNotContain("spotify.exe", names);
    }

    [Fact]
    public void SetDeviceMute_CaptureFlow_UsesInputMutePathNotOutput()
    {
        var legacy = new RecordingAudioManager { InputMuted = false };
        using var adapter = new LegacyAudioEngineAdapter(legacy);

        adapter.SetDeviceMute(DeviceRef.ById("{0.0.1.00000000}.{cap}", AudioFlow.Capture), true);

        Assert.Equal(1, legacy.ToggleMuteInputCallCount);
        Assert.Empty(legacy.ToggleMuteOutputCalls);
    }

    [Fact]
    public void SetDeviceMute_RenderFlow_UsesOutputMutePathNotInput()
    {
        var legacy = new RecordingAudioManager { OutputMuted = false };
        using var adapter = new LegacyAudioEngineAdapter(legacy);

        adapter.SetDeviceMute(DeviceRef.ById("{0.0.0.00000000}.{ren}", AudioFlow.Render), true);

        Assert.Single(legacy.ToggleMuteOutputCalls);
        Assert.Equal(0, legacy.ToggleMuteInputCallCount);
    }

    [Fact]
    public void SetDefaultDevice_ResolvesEndpointIdToFriendlyName()
    {
        var legacy = new RecordingAudioManager
        {
            OutputDevices = new List<AudioDeviceInfo>
            {
                new() { Id = "{0.0.0.00000000}.{abc}", Name = "Headphones" },
                new() { Id = "{0.0.0.00000000}.{def}", Name = "Speakers" },
            }
        };
        using var adapter = new LegacyAudioEngineAdapter(legacy);

        adapter.SetDefaultDevice("{0.0.0.00000000}.{abc}", AudioFlow.Render);

        var name = Assert.Single(legacy.SetDefaultCalls);
        Assert.Equal("Headphones", name);
    }

    [Fact]
    public void SetDefaultDevice_UnresolvedId_DoesNotCallLegacy()
    {
        var legacy = new RecordingAudioManager
        {
            OutputDevices = new List<AudioDeviceInfo>
            {
                new() { Id = "{0.0.0.00000000}.{def}", Name = "Speakers" },
            }
        };
        using var adapter = new LegacyAudioEngineAdapter(legacy);

        adapter.SetDefaultDevice("{0.0.0.00000000}.{does-not-exist}", AudioFlow.Render);

        Assert.Empty(legacy.SetDefaultCalls);
    }

    private sealed class RecordingAudioManager : IAudioManager
    {
        public List<(string? DeviceName, float Volume)> DeviceVolumeCalls { get; } = new();
        public List<(IReadOnlyCollection<string> Names, float Volume)> ProcessVolumeCalls { get; } = new();
        public List<string?> ToggleMuteOutputCalls { get; } = new();
        public List<string> SetDefaultCalls { get; } = new();
        public List<string> ActiveProcessNames { get; set; } = new();
        public List<AudioDeviceInfo> OutputDevices { get; set; } = new();
        public bool InputMuted { get; set; } = false;
        public bool OutputMuted { get; set; } = false;
        public int ToggleMuteInputCallCount { get; private set; }

        public void SetProcessVolume(string processName, float volume)
            => ProcessVolumeCalls.Add((new[] { processName }, volume));
        public void SetProcessVolumes(IReadOnlyCollection<string> processNames, float volume)
            => ProcessVolumeCalls.Add((processNames, volume));
        public void SetDeviceVolume(float volume, string? deviceName = null)
            => DeviceVolumeCalls.Add((deviceName, volume));
        public void ToggleMuteOutput(string? deviceId = null) => ToggleMuteOutputCalls.Add(deviceId);
        public void ToggleMuteInput() => ToggleMuteInputCallCount++;
        public void SetDefaultOutputDevice(string deviceName) => SetDefaultCalls.Add(deviceName);
        public string? GetDefaultOutputDeviceName() => "Speakers";
        public List<AudioDeviceInfo> GetOutputDevices() => OutputDevices;
        public List<AudioDeviceInfo> GetInputDevices() => new();
        public List<string> GetActiveProcessNames() => ActiveProcessNames;
        public bool IsOutputMuted(string? deviceId = null) => OutputMuted;
        public bool IsInputMuted() => InputMuted;
    }
}
