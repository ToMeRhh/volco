using Volco.App.ViewModels;
using Volco.Core.Config;
using Xunit;

namespace Volco.App.Tests.ViewModels;

public class ActionCatalogTests
{
    private static string? Preset(string? id) => id == "gaming" ? "Gaming" : null;

    private static ButtonBindingConfig Binding(ButtonActionConfig press, ButtonActionConfig? hold = null)
        => new() { SinglePress = press, LongPress = hold ?? new() };

    private static readonly ButtonActionConfig Toggle = new()
    {
        Action = ButtonActionType.ToggleOutputDevice,
        Device = new DeviceRefConfig { Id = "spk", FriendlyName = "Speakers (Realtek)" },
        DeviceB = new DeviceRefConfig { Id = "hp", FriendlyName = "Headphones" },
    };

    private static readonly ButtonActionConfig Next = new() { Action = ButtonActionType.NextPreset };
    private static readonly ButtonActionConfig GoToGaming = new() { Action = ButtonActionType.GoToPreset, PresetId = "gaming" };
    private static readonly ButtonActionConfig Mute = new() { Action = ButtonActionType.MuteCurrentOutput };

    [Fact]
    public void Toggle_SaysWhichSideLightsWhat_PerLight()
    {
        Assert.Equal("Dark on Speakers (A), lit on Headphones (B). Follows the Press action.",
            ActionCatalog.LightDescription(Binding(Toggle), singleLed: true, Preset));
        Assert.Equal("A lights for Speakers, B for Headphones. Follows the Press action.",
            ActionCatalog.LightDescription(Binding(Toggle), singleLed: false, Preset));
    }

    [Fact]
    public void Presets_OnOneLed_NextBlinks_GoToIsLitWhileLive()
    {
        Assert.Equal("Dark; blinks when the preset changes. Follows the Press action.",
            ActionCatalog.LightDescription(Binding(Next), singleLed: true, Preset));
        Assert.Equal("On while Gaming is live. Follows the Press action.",
            ActionCatalog.LightDescription(Binding(GoToGaming), singleLed: true, Preset));
        Assert.Equal("Shows the live preset: the first lights A, the second B. Follows the Press action.",
            ActionCatalog.LightDescription(Binding(Next), singleLed: false, Preset));
    }

    [Fact]
    public void OneLedWithAPresetActionOnHold_AlsoBlinks()
    {
        Assert.Equal("On while muted. Follows the Press action. Blinks when Hold changes the preset.",
            ActionCatalog.LightDescription(Binding(Mute, Next), singleLed: true, Preset));
        Assert.Equal("On while muted. Follows the Press action.",
            ActionCatalog.LightDescription(Binding(Mute, Next), singleLed: false, Preset));
    }

    [Fact]
    public void Screen_SaysItFlashesThePresetWhenAnotherSlotChangesIt()
    {
        Assert.Equal("Shows the Press action. Flashes the preset’s name when Hold changes the preset.",
            ActionCatalog.ScreenDescription(Binding(Mute, Next)));
        Assert.Equal("Shows the Press action.", ActionCatalog.ScreenDescription(Binding(Next, GoToGaming)));
        Assert.Equal("Shows the Press action.", ActionCatalog.ScreenDescription(Binding(Mute)));
        Assert.Equal("Blank. None of this button’s actions is set in this preset.",
            ActionCatalog.ScreenDescription(Binding(new ButtonActionConfig())));
    }
}
