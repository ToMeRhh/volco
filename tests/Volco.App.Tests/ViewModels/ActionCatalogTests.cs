using Volco.App.ViewModels;
using Volco.Core.Config;
using Xunit;

namespace Volco.App.Tests.ViewModels;

public class ActionCatalogTests
{
    private static string? Mode(string? id) => id == "gaming" ? "Gaming" : null;

    private static ButtonBindingConfig Binding(ButtonActionConfig press, ButtonActionConfig? hold = null)
        => new() { SinglePress = press, LongPress = hold ?? new() };

    private static readonly ButtonActionConfig Toggle = new()
    {
        Action = ButtonActionType.ToggleOutputDevice,
        Device = new DeviceRefConfig { Id = "spk", FriendlyName = "Speakers (Realtek)" },
        DeviceB = new DeviceRefConfig { Id = "hp", FriendlyName = "Headphones" },
    };

    private static readonly ButtonActionConfig Next = new() { Action = ButtonActionType.NextMode };
    private static readonly ButtonActionConfig GoToGaming = new() { Action = ButtonActionType.GoToMode, ModeId = "gaming" };
    private static readonly ButtonActionConfig Mute = new() { Action = ButtonActionType.MuteCurrentOutput };

    [Fact]
    public void Toggle_SaysWhichSideLightsWhat_PerLight()
    {
        Assert.Equal("Dark on Speakers (A), lit on Headphones (B). Follows the Press action.",
            ActionCatalog.LightDescription(Binding(Toggle), singleLed: true, Mode));
        Assert.Equal("A lights for Speakers, B for Headphones. Follows the Press action.",
            ActionCatalog.LightDescription(Binding(Toggle), singleLed: false, Mode));
    }

    [Fact]
    public void Modes_OnOneLed_NextBlinks_GoToIsLitWhileLive()
    {
        Assert.Equal("Dark; blinks when the mode changes. Follows the Press action.",
            ActionCatalog.LightDescription(Binding(Next), singleLed: true, Mode));
        Assert.Equal("On while Gaming is live. Follows the Press action.",
            ActionCatalog.LightDescription(Binding(GoToGaming), singleLed: true, Mode));
        Assert.Equal("Shows the live mode: the first lights A, the second B. Follows the Press action.",
            ActionCatalog.LightDescription(Binding(Next), singleLed: false, Mode));
    }

    [Fact]
    public void OneLedWithAModeActionOnHold_AlsoBlinks()
    {
        Assert.Equal("On while muted. Follows the Press action. Blinks when Hold changes the mode.",
            ActionCatalog.LightDescription(Binding(Mute, Next), singleLed: true, Mode));
        Assert.Equal("On while muted. Follows the Press action.",
            ActionCatalog.LightDescription(Binding(Mute, Next), singleLed: false, Mode));
    }

    [Fact]
    public void Screen_SaysItFlashesTheModeWhenAnotherSlotChangesIt()
    {
        Assert.Equal("Shows the Press action. Flashes the mode’s name when Hold changes the mode.",
            ActionCatalog.ScreenDescription(Binding(Mute, Next)));
        Assert.Equal("Shows the Press action.", ActionCatalog.ScreenDescription(Binding(Next, GoToGaming)));
        Assert.Equal("Shows the Press action.", ActionCatalog.ScreenDescription(Binding(Mute)));
        Assert.Equal("Blank. None of this button’s actions is set in this mode.",
            ActionCatalog.ScreenDescription(Binding(new ButtonActionConfig())));
    }

    [Fact]
    public void Light_SaysItBlinksTwiceForAnotherAction()
    {
        var play = new ButtonActionConfig { Action = ButtonActionType.MediaPlayPause };
        Assert.Equal("On while muted. Follows the Press action. Blinks twice when you use another of its actions.",
            ActionCatalog.LightDescription(Binding(Mute, play), singleLed: true, Mode));
    }

    [Fact]
    public void Screen_SaysItShowsAnotherActionForAMoment()
    {
        var play = new ButtonActionConfig { Action = ButtonActionType.MediaPlayPause };
        Assert.Equal("Shows the Press action. Shows another action for a moment after you use it.",
            ActionCatalog.ScreenDescription(Binding(Mute, play)));
        Assert.Equal("Shows the Press action. Flashes the mode’s name when Hold changes the mode.",
            ActionCatalog.ScreenDescription(Binding(play, Next)));
    }
}
