using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.Core.Tests.Communication;

public class ControllerStateReporterTests
{
    private static OutputDevicesConfig Pair() => new()
    {
        Device1 = new DeviceRefConfig { Id = "dev-1", FriendlyName = "Speakers" },
        Device2 = new DeviceRefConfig { Id = "dev-2", FriendlyName = "Headphones" },
    };

    private static AudioDevice Dev(string id, string name)
        => new(id, name, AudioFlow.Render, true);

    [Fact]
    public void ResolveActiveDeviceIndex_OnDevice1_ReturnsZero()
        => Assert.Equal(0, ControllerStateReporter.ResolveActiveDeviceIndex(Dev("dev-1", "Speakers"), Pair()));

    [Fact]
    public void ResolveActiveDeviceIndex_OnDevice2_ReturnsOne()
        => Assert.Equal(1, ControllerStateReporter.ResolveActiveDeviceIndex(Dev("dev-2", "Headphones"), Pair()));

    [Fact]
    public void ResolveActiveDeviceIndex_OnNeitherDevice_ReturnsNull()
    {
        // The bug this class exists to fix: today a third device reports as device 2.
        Assert.Null(ControllerStateReporter.ResolveActiveDeviceIndex(Dev("dev-99", "Monitor"), Pair()));
    }

    [Fact]
    public void ResolveActiveDeviceIndex_NoCurrentDevice_ReturnsNull()
        => Assert.Null(ControllerStateReporter.ResolveActiveDeviceIndex(null, Pair()));

    [Fact]
    public void ResolveActiveDeviceIndex_FallsBackToFriendlyNameWhenIdIsBlank()
    {
        var pair = new OutputDevicesConfig
        {
            Device1 = new DeviceRefConfig { Id = "", FriendlyName = "Speakers" },
            Device2 = new DeviceRefConfig { Id = "", FriendlyName = "Headphones" },
        };
        Assert.Equal(1, ControllerStateReporter.ResolveActiveDeviceIndex(Dev("whatever", "Headphones"), pair));
    }

    [Fact]
    public void ResolveActiveDeviceIndex_BothConfiguredDevicesBlank_NeverMatchesOnEmptyString()
    {
        // Regression for the Task 8 repair that used string.Contains("") — always true, so a
        // freshly-created v2 config (empty Id AND empty FriendlyName) reported every device as
        // device 1. Must resolve to null, not 0.
        var pair = new OutputDevicesConfig
        {
            Device1 = new DeviceRefConfig { Id = "", FriendlyName = "" },
            Device2 = new DeviceRefConfig { Id = "", FriendlyName = "" },
        };
        Assert.Null(ControllerStateReporter.ResolveActiveDeviceIndex(Dev("dev-1", "Speakers"), pair));
    }

    [Fact]
    public void BuildFrames_OnNeitherDevice_EmitsNoActiveDeviceFrame()
    {
        var config = AppConfig.CreateDefault();
        config.OutputDevices = Pair();
        var audio = new FakeAudioEngine
        {
            DefaultRender = Dev("dev-99", "Monitor")
        };

        var frames = ControllerStateReporter.BuildFrames(config, audio);

        Assert.DoesNotContain(frames, f => f[2] == (byte)MessageType.ActiveDevice);
    }

    [Fact]
    public void BuildFrames_EmitsMuteStateForMuteButtons()
    {
        var config = AppConfig.CreateDefault();
        config.OutputDevices = Pair();
        config.Buttons[0].SinglePress = new ButtonActionConfig { Action = ButtonActionType.MuteCurrentOutput };

        var audio = new FakeAudioEngine { DefaultRender = Dev("dev-1", "Speakers") };
        audio.DeviceStates["dev-1"] = (0.5f, true);

        var frames = ControllerStateReporter.BuildFrames(config, audio);

        var mute = Assert.Single(frames, f => f[2] == (byte)MessageType.MuteState);
        Assert.Equal(0, mute[3]);       // button index
        Assert.Equal(1, mute[4]);       // muted
    }

    [Fact]
    public void BuildFrames_ButtonWithNoMuteAction_EmitsNoMuteFrame()
    {
        var config = AppConfig.CreateDefault();
        config.OutputDevices = Pair();
        config.Buttons[0].SinglePress = new ButtonActionConfig { Action = ButtonActionType.ToggleOutputDevice };
        config.Buttons[1].SinglePress = new ButtonActionConfig { Action = ButtonActionType.None };

        var audio = new FakeAudioEngine { DefaultRender = Dev("dev-1", "Speakers") };

        var frames = ControllerStateReporter.BuildFrames(config, audio);

        Assert.DoesNotContain(frames, f => f[2] == (byte)MessageType.MuteState);
    }
}
