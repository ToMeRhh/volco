using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Volco.App.Controls;
using Xunit;

namespace Volco.App.Tests.Controls;

public class SettingsCardTests
{
    private static Window Show(SettingsCard card)
    {
        var window = new Window { Content = card };
        window.Show();
        return window;
    }

    [AvaloniaFact]
    public void Card_ShowsHeaderDescriptionAndContent()
    {
        var toggle = new ToggleSwitch();
        var card = new SettingsCard { Header = "Debug Logging", Description = "Verbose output", Content = toggle };
        var window = Show(card);

        var header = card.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(x => x.Name == "PART_Header");
        var description = card.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(x => x.Name == "PART_Description");

        Assert.NotNull(header);
        Assert.Equal("Debug Logging", header!.Text);
        Assert.NotNull(description);
        Assert.Equal("Verbose output", description!.Text);
        Assert.True(description.IsVisible);
        Assert.True(toggle.IsVisible);
        Assert.Contains(toggle, window.GetVisualDescendants());
    }

    [AvaloniaFact]
    public void Card_WithNullDescription_HidesDescription()
    {
        var card = new SettingsCard { Header = "Device 1", Description = null, Content = new ToggleSwitch() };
        Show(card);

        var description = card.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(x => x.Name == "PART_Description");

        Assert.NotNull(description);
        Assert.False(description!.IsVisible);
    }

    [AvaloniaFact]
    public void Card_WithoutHeaderIcon_HidesIconSlot()
    {
        var card = new SettingsCard { Header = "x" };
        Show(card);

        var icon = card.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault(x => x.Name == "PART_HeaderIcon");

        Assert.NotNull(icon);
        Assert.False(icon!.IsVisible);
    }
}
