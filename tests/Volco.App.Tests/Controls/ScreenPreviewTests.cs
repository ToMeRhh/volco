using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Volco.App.Controls;
using Volco.Core.Communication;
using Xunit;

namespace Volco.App.Tests.Controls;

public class ScreenPreviewTests
{
    private static ScreenPreview Show(ScreenContent? content, double scale = 1)
    {
        var preview = new ScreenPreview { Screen = content, Scale = scale };
        new Window { Content = preview, Width = 400, Height = 300 }.Show();
        Dispatcher.UIThread.RunJobs();
        return preview;
    }

    private static T Part<T>(ScreenPreview p, string name) where T : Control => p.FindControl<T>(name)!;

    [AvaloniaFact]
    public void TextTop_IconWithSlash_AndMain()
    {
        var p = Show(ScreenContent.Text("MUTED", ScreenIcon.Mic, muted: true, "Microphone"));
        Assert.Equal("MUTED", Part<TextBlock>(p, "TopText").Text);
        Assert.True(Part<TextBlock>(p, "TopText").IsVisible);
        Assert.False(Part<ItemsControl>(p, "TopDots").IsVisible);
        Assert.NotNull(Part<Image>(p, "IconImage").Source);
        Assert.True(Part<Panel>(p, "Slash").IsVisible);
        Assert.Equal("Microphone", Part<TextBlock>(p, "MainText").Text);
    }

    [AvaloniaFact]
    public void Dots_OneFilledPerSelectedPreset()
    {
        var p = Show(ScreenContent.Dots(1, 3, ScreenIcon.Presets, "Gaming"));
        var dots = Part<ItemsControl>(p, "TopDots");
        Assert.True(dots.IsVisible);
        Assert.Equal(new[] { false, true, false }, dots.ItemsSource!.Cast<bool>());
        Assert.False(Part<Panel>(p, "Slash").IsVisible);
    }

    [AvaloniaFact]
    public void Blank_HidesTheIcon_AndEmptyTexts()
    {
        var p = Show(ScreenContent.Blank);
        Assert.False(Part<Image>(p, "IconImage").IsVisible);
        Assert.Equal("", Part<TextBlock>(p, "MainText").Text);
        Assert.False(Part<TextBlock>(p, "TopText").IsVisible);
    }

    [AvaloniaFact]
    public void Scale_IsALayoutTransform()
    {
        var p = Show(ScreenContent.Blank, scale: 2);
        var scale = Assert.IsType<ScaleTransform>(Part<LayoutTransformControl>(p, "Scaler").LayoutTransform);
        Assert.Equal(2, scale.ScaleX);
    }

    [AvaloniaFact]
    public void EveryIcon_LoadsItsBitmap()
    {
        foreach (var icon in Enum.GetValues<ScreenIcon>())
            Assert.NotNull(ScreenIcons.Bitmap(icon));
    }

    [AvaloniaFact]
    public void UndefinedIcon_FallsBackToGeneric()
    {
        Assert.Same(ScreenIcons.Bitmap(ScreenIcon.Generic), ScreenIcons.Bitmap((ScreenIcon)42));
    }
}
