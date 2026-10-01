using Volco.Core.Config;

namespace Volco.App.Tests;

internal static class TestConfigExtensions
{
    /// <summary>The button's binding in the Default preset (created if missing) - what a
    /// schema v2 test meant by <c>button.SinglePress</c> and friends.</summary>
    public static ButtonBindingConfig Default(this ButtonConfig button)
        => button.GetOrAddBinding(AppConfig.DefaultPresetId);
}
