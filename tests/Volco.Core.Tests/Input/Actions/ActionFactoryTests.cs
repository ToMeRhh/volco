using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Input.Actions;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.Core.Tests.Input.Actions;

public class ActionFactoryTests
{
    private readonly FakeAudioEngine _audio = new();
    private readonly FakeModeController _modes = new();

    [Fact]
    public void Create_MuteCurrentOutput_ReturnsCorrectType()
    {
        var config = new ButtonActionConfig { Action = ButtonActionType.MuteCurrentOutput };
        var action = ActionFactory.Create(config, _audio, _modes);
        Assert.IsType<MuteCurrentOutputAction>(action);
    }

    [Fact]
    public void Create_MuteSpecificDevice_ReturnsCorrectType()
    {
        var config = new ButtonActionConfig { Action = ButtonActionType.MuteSpecificDevice, Device = new DeviceRefConfig { FriendlyName = "Speakers" } };
        var action = ActionFactory.Create(config, _audio, _modes);
        Assert.IsType<MuteSpecificDeviceAction>(action);
    }

    [Fact]
    public void Create_None_ReturnsNull()
    {
        var config = new ButtonActionConfig { Action = ButtonActionType.None };
        var action = ActionFactory.Create(config, _audio, _modes);
        Assert.Null(action);
    }

    [Fact]
    public void Create_ToggleOutputDevice_ReturnsCorrectType()
    {
        var config = new ButtonActionConfig { Action = ButtonActionType.ToggleOutputDevice };
        var action = ActionFactory.Create(config, _audio, _modes);
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

        ActionFactory.Create(config, audio, _modes)?.Execute();

        Assert.Empty(audio.ToggleMuteCalls);
    }

    [Fact]
    public void MuteSpecificDevice_WithNullDeviceRef_DoesNothing()
    {
        var audio = new FakeAudioEngine();
        var config = new ButtonActionConfig { Action = ButtonActionType.MuteSpecificDevice, Device = null };

        ActionFactory.Create(config, audio, _modes)?.Execute();

        Assert.Empty(audio.ToggleMuteCalls);
    }

    [Fact]
    public void SetOutputDevice_WithEmptyDeviceRef_DoesNothing()
    {
        var audio = new FakeAudioEngine();
        var config = new ButtonActionConfig { Action = ButtonActionType.SetOutputDevice, Device = new DeviceRefConfig() };

        ActionFactory.Create(config, audio, _modes)?.Execute();

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

        ActionFactory.Create(config, audio, _modes)!.Execute();

        Assert.Equal(DeviceRef.ById("dev-1"), Assert.Single(audio.ToggleMuteCalls));
    }

    [Fact]
    public void ToggleOutputDevice_WhenOnDeviceA_SwitchesToDeviceB()
    {
        var audio = new FakeAudioEngine
        {
            DefaultRender = new AudioDevice("dev-1", "Speakers", AudioFlow.Render, true)
        };
        var config = Toggle(new DeviceRefConfig { Id = "dev-1", FriendlyName = "Speakers" },
                            new DeviceRefConfig { Id = "dev-2", FriendlyName = "Headphones" });
        ActionFactory.Create(config, audio, _modes)!.Execute();

        var (endpointId, flow) = Assert.Single(audio.SetDefaultCalls);
        Assert.Equal("dev-2", endpointId);
        Assert.Equal(AudioFlow.Render, flow);
    }

    [Fact]
    public void ToggleOutputDevice_WhenOnNeitherDevice_SwitchesToDeviceA()
    {
        var audio = new FakeAudioEngine
        {
            DefaultRender = new AudioDevice("dev-99", "Monitor", AudioFlow.Render, true)
        };
        ActionFactory.Create(Toggle(new DeviceRefConfig { Id = "dev-1", FriendlyName = "Speakers" },
                                    new DeviceRefConfig { Id = "dev-2", FriendlyName = "Headphones" }),
                             audio, _modes)!.Execute();

        Assert.Equal("dev-1", Assert.Single(audio.SetDefaultCalls).EndpointId);
    }

    /// <summary>
    /// Review item 11: ToggleOutputDeviceAction.DeviceMatches' FriendlyName-fallback branch
    /// (used when a configured device has Id == "") had no test - and review item 5 shows that
    /// exact config shape is reachable in practice (a migration that ran against an empty device
    /// list, before its own fix, stamped v2 with every reference blanked to {Id:"", FriendlyName}
    /// forever). Device A is configured with a blank Id and a populated FriendlyName only; the
    /// current default device matches it by name, so toggling must target device B by id.
    /// </summary>
    [Fact]
    public void ToggleOutputDevice_DeviceAConfiguredByFriendlyNameOnly_MatchesAndSwitchesToDeviceB()
    {
        var audio = new FakeAudioEngine
        {
            DefaultRender = new AudioDevice("dev-1", "Speakers", AudioFlow.Render, true)
        };
        var config = Toggle(new DeviceRefConfig { Id = "", FriendlyName = "Speakers" },
                            new DeviceRefConfig { Id = "dev-2", FriendlyName = "Headphones" });
        ActionFactory.Create(config, audio, _modes)!.Execute();

        var (endpointId, flow) = Assert.Single(audio.SetDefaultCalls);
        Assert.Equal("dev-2", endpointId);
        Assert.Equal(AudioFlow.Render, flow);
    }

    private static ButtonActionConfig Toggle(DeviceRefConfig a, DeviceRefConfig b)
        => new() { Action = ButtonActionType.ToggleOutputDevice, Device = a, DeviceB = b };

    [Fact]
    public void ToggleOutputDevice_WhenOnDeviceB_SwitchesBackToDeviceA()
    {
        var audio = new FakeAudioEngine
        {
            DefaultRender = new AudioDevice("dev-2", "Headphones", AudioFlow.Render, true)
        };

        ActionFactory.Create(Toggle(new DeviceRefConfig { Id = "dev-1", FriendlyName = "Speakers" },
                                    new DeviceRefConfig { Id = "dev-2", FriendlyName = "Headphones" }),
                             audio, _modes)!.Execute();

        Assert.Equal("dev-1", Assert.Single(audio.SetDefaultCalls).EndpointId);
    }

    [Fact]
    public void ToggleOutputDevice_WithNoPairConfigured_DoesNothing()
    {
        var audio = new FakeAudioEngine
        {
            DefaultRender = new AudioDevice("dev-1", "Speakers", AudioFlow.Render, true)
        };

        ActionFactory.Create(new ButtonActionConfig { Action = ButtonActionType.ToggleOutputDevice },
                             audio, _modes)!.Execute();

        Assert.Empty(audio.SetDefaultCalls);
    }

    [Fact]
    public void NextMode_AdvancesTheModeController()
    {
        ActionFactory.Create(new ButtonActionConfig { Action = ButtonActionType.NextMode }, _audio, _modes)!.Execute();

        Assert.Equal(1, _modes.NextCalls);
    }

    [Fact]
    public void GoToMode_SetsThatMode()
    {
        ActionFactory.Create(new ButtonActionConfig { Action = ButtonActionType.GoToMode, ModeId = "gaming" },
                             _audio, _modes)!.Execute();

        Assert.Equal(new[] { "gaming" }, _modes.SetCalls);
    }

    [Fact]
    public void GoToMode_WithNoModeChosen_DoesNothing()
    {
        ActionFactory.Create(new ButtonActionConfig { Action = ButtonActionType.GoToMode }, _audio, _modes)!.Execute();

        Assert.Empty(_modes.SetCalls);
    }

    [Theory]
    [InlineData(ButtonActionType.NextMode)]
    [InlineData(ButtonActionType.GoToMode)]
    public void ModeActions_WithoutAModeController_AreNull(ButtonActionType type)
        => Assert.Null(ActionFactory.Create(new ButtonActionConfig { Action = type }, _audio, modes: null));
}

internal sealed class FakeModeController : Volco.Core.Runtime.IModeController
{
    public string ActiveModeId { get; private set; } = AppConfig.DefaultModeId;
    public List<string> SetCalls { get; } = new();
    public int NextCalls { get; private set; }

    public void SetActiveMode(string modeId)
    {
        SetCalls.Add(modeId);
        ActiveModeId = modeId;
    }

    public void NextMode() => NextCalls++;
}
