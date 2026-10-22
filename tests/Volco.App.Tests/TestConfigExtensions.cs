using Volco.Core.Config;

namespace Volco.App.Tests;

internal static class TestConfigExtensions
{
    /// <summary>The button's binding in the Default mode (created if missing) - what a
    /// schema v2 test meant by <c>button.SinglePress</c> and friends.</summary>
    public static ButtonBindingConfig Default(this ButtonConfig button)
        => button.GetOrAddBinding(AppConfig.DefaultModeId);
}

internal static class TestDraftExtensions
{
    /// <summary>Sets slider <paramref name="slider"/>'s pin in the Hardware draft, as its pin field would.</summary>
    public static void SetSliderPin(this Volco.App.ViewModels.ControllerViewModel c, int slider, uint pin)
    {
        c.Draft.Sliders[slider].Pin = pin;
        c.OnDraftEdited();
    }

    /// <summary>Sets button <paramref name="button"/>'s pin in the Hardware draft, as its pin field would.</summary>
    public static void SetButtonPin(this Volco.App.ViewModels.ControllerViewModel c, int button, uint pin)
    {
        c.Draft.Buttons[button].Pin = pin;
        c.OnDraftEdited();
    }
}
