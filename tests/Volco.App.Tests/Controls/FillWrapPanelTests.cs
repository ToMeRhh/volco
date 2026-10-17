using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Volco.App.Controls;
using Xunit;

namespace Volco.App.Tests.Controls;

public class FillWrapPanelTests
{
    private static (FillWrapPanel Panel, List<Border> Items) Host(int count, double width, double min = 84)
    {
        var panel = new FillWrapPanel { MinItemWidth = min, Spacing = 6 };
        var items = new List<Border>();
        for (var i = 0; i < count; i++)
        {
            var b = new Border { Height = 40 };
            items.Add(b);
            panel.Children.Add(b);
        }
        var window = new Window { Width = width, Height = 400, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (panel, items);
    }

    [AvaloniaFact]
    public void FiveItems_Width560_OneRowOfFive_EqualWidths()
    {
        var (_, items) = Host(5, 560);
        Assert.All(items, i => Assert.Equal(0, i.Bounds.Y));
        Assert.All(items, i => Assert.Equal(items[0].Bounds.Width, i.Bounds.Width, 3));
        Assert.Equal(560 - 0, items[4].Bounds.Right, 1);
    }

    [AvaloniaFact]
    public void TwelveItems_Width560_MinWidth74_WrapsToTwoRows()
    {
        var (_, items) = Host(12, 560, 74);
        Assert.Equal(7, items.Count(i => i.Bounds.Y == 0));
        Assert.Equal(5, items.Count(i => i.Bounds.Y > 0));
    }

    [AvaloniaFact]
    public void ThreeItems_StretchAcross()
    {
        var (_, items) = Host(3, 560);
        Assert.All(items, i => Assert.Equal((560 - 12) / 3.0, i.Bounds.Width, 1));
    }

    [AvaloniaFact]
    public void Narrower_Than_Min_OneColumn()
    {
        var (_, items) = Host(3, 60);
        Assert.Equal(new[] { 0.0, 46.0, 92.0 }, items.Select(i => i.Bounds.Y).ToArray());
        Assert.All(items, i => Assert.Equal(0, i.Bounds.X));
    }
}
