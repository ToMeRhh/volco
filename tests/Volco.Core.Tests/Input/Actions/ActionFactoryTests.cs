using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Input.Actions;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.Core.Tests.Input.Actions;

public class ActionFactoryTests
{
    private readonly FakeAudioEngine _audio = new();
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
        var config = new ButtonActionConfig { Action = ButtonActionType.MuteSpecificDevice, Device = new DeviceRefConfig { FriendlyName = "Speakers" } };
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

    [Fact]
    public void MuteSpecificDevice_WithEmptyDeviceRef_DoesNothing()
    {
        var audio = new FakeAudioEngine();
        var config = new ButtonActionConfig
        {
            Action = ButtonActionType.MuteSpecificDevice,
            Device = new DeviceRefConfig()          // both Id and FriendlyName empty
        };

        ActionFactory.Create(config, audio, _configManager)?.Execute();

        Assert.Empty(audio.ToggleMuteCalls);
    }

    [Fact]
    public void MuteSpecificDevice_WithNullDeviceRef_DoesNothing()
    {
        var audio = new FakeAudioEngine();
        var config = new ButtonActionConfig { Action = ButtonActionType.MuteSpecificDevice, Device = null };

        ActionFactory.Create(config, audio, _configManager)?.Execute();

        Assert.Empty(audio.ToggleMuteCalls);
    }

    [Fact]
    public void SetOutputDevice_WithEmptyDeviceRef_DoesNothing()
    {
        var audio = new FakeAudioEngine();
        var config = new ButtonActionConfig { Action = ButtonActionType.SetOutputDevice, Device = new DeviceRefConfig() };

        ActionFactory.Create(config, audio, _configManager)?.Execute();

        Assert.Empty(audio.SetDefaultCalls);
    }

    [Fact]
    public void MuteSpecificDevice_WithId_TogglesThatDevice()
    {
        var audio = new FakeAudioEngine();
        var config = new ButtonActionConfig
        {
            Action = ButtonActionType.MuteSpecificDevice,
            Device = new DeviceRefConfig { Id = "dev-1", FriendlyName = "Headphones" }
        };

        ActionFactory.Create(config, audio, _configManager)!.Execute();

        Assert.Equal(DeviceRef.ById("dev-1"), Assert.Single(audio.ToggleMuteCalls));
    }

    [Fact]
    public void ToggleOutputDevice_WhenOnDevice1_SwitchesToDevice2()
    {
        var audio = new FakeAudioEngine
        {
            DefaultRender = new AudioDevice("dev-1", "Speakers", AudioFlow.Render, true, true, true)
        };
        _configManager.Config.OutputDevices.Device1 = new DeviceRefConfig { Id = "dev-1", FriendlyName = "Speakers" };
        _configManager.Config.OutputDevices.Device2 = new DeviceRefConfig { Id = "dev-2", FriendlyName = "Headphones" };

        var config = new ButtonActionConfig { Action = ButtonActionType.ToggleOutputDevice };
        ActionFactory.Create(config, audio, _configManager)!.Execute();

        var (endpointId, flow) = Assert.Single(audio.SetDefaultCalls);
        Assert.Equal("dev-2", endpointId);
        Assert.Equal(AudioFlow.Render, flow);
    }

    [Fact]
    public void ToggleOutputDevice_WhenOnNeitherDevice_SwitchesToDevice1()
    {
        var audio = new FakeAudioEngine
        {
            DefaultRender = new AudioDevice("dev-99", "Monitor", AudioFlow.Render, true, true, true)
        };
        _configManager.Config.OutputDevices.Device1 = new DeviceRefConfig { Id = "dev-1", FriendlyName = "Speakers" };
        _configManager.Config.OutputDevices.Device2 = new DeviceRefConfig { Id = "dev-2", FriendlyName = "Headphones" };

        ActionFactory.Create(new ButtonActionConfig { Action = ButtonActionType.ToggleOutputDevice },
                             audio, _configManager)!.Execute();

        Assert.Equal("dev-1", Assert.Single(audio.SetDefaultCalls).EndpointId);
    }

    /// <summary>
    /// Review item 11: ToggleOutputDeviceAction.DeviceMatches' FriendlyName-fallback branch
    /// (used when a configured device has Id == "") had no test - and review item 5 shows that
    /// exact config shape is reachable in practice (a migration that ran against an empty device
    /// list, before its own fix, stamped v2 with every reference blanked to {Id:"", FriendlyName}
    /// forever). Device1 is configured with a blank Id and a populated FriendlyName only; the
    /// current default device matches it by name, so toggling must target Device2 by id.
    /// </summary>
    [Fact]
    public void ToggleOutputDevice_Device1ConfiguredByFriendlyNameOnly_MatchesAndSwitchesToDevice2()
    {
        var audio = new FakeAudioEngine
        {
            DefaultRender = new AudioDevice("dev-1", "Speakers", AudioFlow.Render, true, true, true)
        };
        _configManager.Config.OutputDevices.Device1 = new DeviceRefConfig { Id = "", FriendlyName = "Speakers" };
        _configManager.Config.OutputDevices.Device2 = new DeviceRefConfig { Id = "dev-2", FriendlyName = "Headphones" };

        var config = new ButtonActionConfig { Action = ButtonActionType.ToggleOutputDevice };
        ActionFactory.Create(config, audio, _configManager)!.Execute();

        var (endpointId, flow) = Assert.Single(audio.SetDefaultCalls);
        Assert.Equal("dev-2", endpointId);
        Assert.Equal(AudioFlow.Render, flow);
    }
}
