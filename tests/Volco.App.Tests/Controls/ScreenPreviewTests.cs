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
    public void Dots_OneFilledPerSelectedMode()
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

    [AvaloniaFact]
    public void EveryIcon_HasDistinctVectorArt()
    {
        var geometries = Enum.GetValues<ScreenIcon>().Select(ScreenIcons.Geometry).ToList();
        Assert.All(geometries, Assert.NotNull);
        Assert.Equal(geometries.Count, geometries.Distinct().Count());
    }

    [AvaloniaFact]
    public void UndefinedIcon_FallsBackToGenericVectorArt()
    {
        Assert.Same(ScreenIcons.Geometry(ScreenIcon.Generic), ScreenIcons.Geometry((ScreenIcon)42));
    }

    [AvaloniaFact]
    public void AnimatedIcon_ShowsItsFramesInTurn()
    {
        var p = Show(ScreenContent.Text("PLAYING", ScreenIcon.Equalizer, false, "Spotify"));
        // New content restarts at frame 0, whatever the real timer did while the window opened.
        p.Screen = ScreenContent.Text("PLAYING", ScreenIcon.Equalizer, false, "Chrome");
        var image = Part<Image>(p, "IconImage");
        Assert.True(p.Animating);
        Assert.Same(ScreenIcons.Bitmap(ScreenIcon.Equalizer, 0), image.Source);
        p.NextFrame();
        Assert.Same(ScreenIcons.Bitmap(ScreenIcon.Equalizer, 1), image.Source);
        for (var i = 0; i < 3; i++) p.NextFrame();
        Assert.Same(ScreenIcons.Bitmap(ScreenIcon.Equalizer, 0), image.Source);   // 4 frames, round again
    }

    [AvaloniaFact]
    public void StillIcon_DoesNotAnimate_AndAChangeStopsTheAnimation()
    {
        var p = Show(ScreenContent.Text("PLAYING", ScreenIcon.Equalizer, false, "Spotify"));
        p.NextFrame();
        p.Screen = ScreenContent.Text("PAUSED", ScreenIcon.EqualizerStill, false, "Spotify");
        Dispatcher.UIThread.RunJobs();
        Assert.False(p.Animating);
        Assert.Same(ScreenIcons.Bitmap(ScreenIcon.EqualizerStill), Part<Image>(p, "IconImage").Source);
    }

    [AvaloniaFact]
    public void LeavingTheWindow_StopsTheAnimation()
    {
        var p = Show(ScreenContent.Text("PLAYING", ScreenIcon.Equalizer, false, "Spotify"));
        ((Window)p.Parent!).Content = null;
        Dispatcher.UIThread.RunJobs();
        Assert.False(p.Animating);
    }

    [AvaloniaFact]
    public void EveryFrame_LoadsItsBitmap()
    {
        foreach (var icon in Enum.GetValues<ScreenIcon>())
            for (var f = 0; f < ScreenIconSets.FrameCount(icon); f++)
                Assert.NotNull(ScreenIcons.Bitmap(icon, f));
        Assert.NotSame(ScreenIcons.Bitmap(ScreenIcon.Equalizer, 0), ScreenIcons.Bitmap(ScreenIcon.Equalizer, 1));
    }

    [AvaloniaFact]
    public void HiddenWindow_StopsTheAnimation_AndShowingItRestartsIt()
    {
        // Closing Settings only hides the window: its previews stay attached.
        var p = Show(ScreenContent.Text("PLAYING", ScreenIcon.Equalizer, false, "Spotify"));
        var window = (Window)p.Parent!;
        window.Hide();
        Dispatcher.UIThread.RunJobs();
        Assert.False(p.Animating);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(p.Animating);
    }

    [AvaloniaFact]
    public void HiddenPreview_DoesNotAnimate()
    {
        // A button with LEDs keeps its (hidden) screen preview.
        var p = Show(ScreenContent.Text("PLAYING", ScreenIcon.Equalizer, false, "Spotify"));
        p.IsVisible = false;
        Assert.False(p.Animating);
        p.IsVisible = true;
        Assert.True(p.Animating);
    }
}
