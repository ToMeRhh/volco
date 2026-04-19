using Volco.Audio;
using Volco.Config;
using Volco.Input.Actions;

namespace Volco.Tests.Input.Actions;

public class ActionFactoryTests
{
    private readonly FakeAudioManager _audio = new();
    private readonly ConfigManager _configManager;

    public ActionFactoryTests()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"volco-test-{Guid.NewGuid()}", "config.json");
        _configManager = new ConfigManager(tempPath);
        _configManager.Load();
    }

    [Fact]
    public void Create_MuteCurrentOutput_ReturnsCorrectType()
    {
        var config = new ButtonActionConfig { Action = ButtonActionType.MuteCurrentOutput };
        var action = ActionFactory.Create(config, _audio, _configManager);
        Assert.IsType<MuteCurrentOutputAction>(action);
    }

    [Fact]
    public void Create_MuteSpecificDevice_ReturnsCorrectType()
    {
        var config = new ButtonActionConfig { Action = ButtonActionType.MuteSpecificDevice, DeviceId = "Speakers" };
        var action = ActionFactory.Create(config, _audio, _configManager);
        Assert.IsType<MuteSpecificDeviceAction>(action);
    }

    [Fact]
    public void Create_None_ReturnsNull()
    {
        var config = new ButtonActionConfig { Action = ButtonActionType.None };
        var action = ActionFactory.Create(config, _audio, _configManager);
        Assert.Null(action);
    }

    [Fact]
    public void Create_ToggleOutputDevice_ReturnsCorrectType()
    {
        var config = new ButtonActionConfig { Action = ButtonActionType.ToggleOutputDevice };
        var action = ActionFactory.Create(config, _audio, _configManager);
        Assert.IsType<ToggleOutputDeviceAction>(action);
    }

    private class FakeAudioManager : IAudioManager
    {
        public void SetProcessVolume(string processName, float volume) { }
        public void SetDeviceVolume(float volume) { }
        public void ToggleMuteOutput(string? deviceId = null) { }
        public void ToggleMuteInput() { }
        public void SetDefaultOutputDevice(string deviceName) { }
        public List<AudioDeviceInfo> GetOutputDevices() => new();
        public List<AudioDeviceInfo> GetInputDevices() => new();
        public List<string> GetActiveProcessNames() => new();
    }
}
