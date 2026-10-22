using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.Controls;
using Volco.App.Views.Pages;
using Volco.App.Views.Stage;
using Volco.Core.Audio;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.App.Tests.Pages;

/// <summary>A tile flashes when the controller reports its control while the page shows, and
/// never just because the page is shown again (<see cref="TileFlash"/>).</summary>
public class TileFlashTests
{
    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static bool Flashing(Control tile)
        => tile.GetVisualDescendants().OfType<Panel>().First(p => p.Name == "TileRoot").Classes.Contains("flash");

    private static bool PumpUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            using var slice = new CancellationTokenSource();
            slice.CancelAfter(10);
            Dispatcher.UIThread.MainLoop(slice.Token);
        }
        return condition();
    }

    [AvaloniaFact]
    public void Report_Flashes_ThenStops_ShowingThePageAgainDoesNot()
    {
        var speakers = new AudioDevice("dev-1", "Speakers", AudioFlow.Render, true);
        using var rig = new TestRig(new FakeAudioEngine { Devices = { speakers }, DefaultRender = speakers });
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new StagePage(vm.Controls);
        var window = new Window { Content = page, Width = 932, Height = 680 };
        window.Show();
        Settle(window);
        SliderTile Slider() => page.GetVisualDescendants().OfType<SliderTile>().First();
        ButtonTile Button() => page.GetVisualDescendants().OfType<ButtonTile>().First();

        vm.Sliders.Items[0].Pulse();
        vm.Buttons.Items[0].Pulse();
        Assert.True(Flashing(Slider()));
        Assert.True(Flashing(Button()));
        Assert.True(PumpUntil(() => !Flashing(Slider()) && !Flashing(Button())), "the flash did not end");

        // Another page, then the Controls page again (pages are cached): the items are still
        // active from their reports, and nothing flashes.
        window.Content = new TextBlock();
        Settle(window);
        window.Content = page;
        Settle(window);
        Assert.True(vm.Sliders.Items[0].IsActive);
        Assert.False(Flashing(Slider()));
        Assert.False(Flashing(Button()));

        // A report while hidden does not flash later either.
        window.Content = new TextBlock();
        Settle(window);
        vm.Buttons.Items[0].Pulse();
        window.Content = page;
        Settle(window);
        Assert.False(Flashing(Button()));
        window.Close();
    }
}
