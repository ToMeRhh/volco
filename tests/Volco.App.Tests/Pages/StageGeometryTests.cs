using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.Controls;
using Volco.App.ViewModels;
using Volco.App.ViewModels.Hardware;
using Volco.App.Views.Stage;
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Input.Actions;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.App.Tests.Pages;

/// <summary>The Controller page's drawing (controller-page spec §4.1): the title row, the tiles'
/// Controls layer with the wiring captions hidden, and the side panel.</summary>
public sealed class StageGeometryTests : IDisposable
{
    public StageGeometryTests() => StageTransitions.Enabled = false;

    public void Dispose() => StageTransitions.Enabled = false;

    private sealed record Shown(StagePage Page, Window Window, TestRig Rig, SettingsViewModel Vm) : IDisposable
    {
        public void Dispose()
        {
            Window.Close();
            Vm.Dispose();
            Rig.Dispose();
        }
    }

    private static readonly string[] IndicatorTexts = { "12", "18 · 19", "Bus 0 · 3C", "" };

    /// <summary>The stage in a 1100 × 720 window, every tile with its wiring.</summary>
    private static Shown Show(int sliders, params IndicatorKind[] buttons) => Show(true, sliders, buttons);

    /// <summary>The stage in a 1100 × 720 window; <paramref name="wired"/>: every tile gets its wiring.</summary>
    private static Shown Show(bool wired, int sliders, params IndicatorKind[] buttons)
    {
        var speakers = new AudioDevice("dev-1", "Living room surround speakers", AudioFlow.Render, true);
        var engine = new FakeAudioEngine
        {
            Devices = { speakers, new AudioDevice("dev-2", "Wireless noise cancelling headphones", AudioFlow.Render, false) },
            DefaultRender = speakers,
        };
        engine.DeviceStates["dev-1"] = (0.5f, false);
        var rig = new TestRig(engine);
        var c = rig.Config.Config;
        c.Controller = new ControllerInfoConfig { ProtocolVersion = 1, Sliders = sliders, Buttons = buttons.ToList() };
        while (c.Sliders.Count < sliders) c.Sliders.Add(new SliderConfig { Index = c.Sliders.Count, Type = SliderMappingType.None });
        while (c.Buttons.Count < buttons.Length) c.Buttons.Add(new ButtonConfig { Index = c.Buttons.Count });
        c.Buttons[0].Default().SinglePress = new ButtonActionConfig
        {
            Action = ButtonActionType.ToggleOutputDevice,
            Device = new DeviceRefConfig { Id = "dev-1", FriendlyName = "Living room surround speakers" },
            DeviceB = new DeviceRefConfig { Id = "dev-2", FriendlyName = "Wireless noise cancelling headphones" },
        };

        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new StagePage(vm.Controls);
        var window = new Window { Content = page, Width = 1100, Height = 720 };
        window.Show();
        // The Hardware view model wires every Controls tile; an unwired tile is one it doesn't draw.
        for (var i = 0; !wired && i < vm.Sliders.Items.Count; i++) vm.Sliders.Items[i].Wiring = null;
        for (var i = 0; !wired && i < vm.Buttons.Items.Count; i++) vm.Buttons.Items[i].Wiring = null;
        for (var i = 0; wired && i < vm.Sliders.Items.Count; i++)
            vm.Sliders.Items[i].Wiring = new TileWiring { Title = $"Slider {i + 1}", PinText = $"GPIO {33 + i}", RawLevel = 0.3 };
        for (var i = 0; wired && i < vm.Buttons.Items.Count; i++)
            vm.Buttons.Items[i].Wiring = new TileWiring
            {
                Title = $"Button {i + 1}", PinText = $"GPIO {13 + i}", IndicatorText = IndicatorTexts[i % IndicatorTexts.Length],
            };
        Settle(window);
        return new Shown(page, window, rig, vm);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static List<Control> Tiles(Visual root)
        => root.GetVisualDescendants().Where(v => v is SliderTile or ButtonTile).Cast<Control>().ToList();

    private static List<Rect> TileRects(Shown s)
        => Tiles(s.Page).Select(t => new Rect(t.TranslatePoint(new Point(0, 0), s.Page)!.Value, t.Bounds.Size)).ToList();

    private static T In<T>(Visual root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    /// <summary>The opacity it is drawn with: its own times its ancestors'.</summary>
    private static double DrawnOpacity(Visual visual)
    {
        var opacity = 1.0;
        for (var v = visual; v is not null; v = v.GetVisualParent()) opacity *= v.Opacity;
        return opacity;
    }

    [AvaloniaFact]
    public void TitleRowSaysController_At32Px()
    {
        using var s = Show(5, IndicatorKind.Led, IndicatorKind.LedPair, IndicatorKind.Screen, IndicatorKind.None);
        var row = In<Grid>(s.Page, "TitleRow");
        Assert.Equal("Controller", In<TextBlock>(row, "PageTitle").Text);
        Assert.Equal(32, row.Bounds.Height, 0.5);
        Assert.DoesNotContain("hardware", s.Page.Classes);
    }

    /// <summary>Tiles don't show pins (controller-page spec §4.1): the wiring captions are hidden
    /// and keep their measure; the actions, the lights and the grips show.</summary>
    [AvaloniaFact]
    public void TilesShowTheirActions_NotTheirWiring()
    {
        using var s = Show(5, IndicatorKind.Led, IndicatorKind.LedPair, IndicatorKind.Screen, IndicatorKind.None);
        var tiles = Tiles(s.Page);
        var pin = tiles[0].GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "GPIO 33");
        var indicator = tiles[6].GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "18 · 19");
        Assert.Equal(0, DrawnOpacity(pin));
        Assert.Equal(0, DrawnOpacity(indicator));
        Assert.Equal(1, DrawnOpacity(In<TextBlock>(tiles[5], "MainAction")));
        Assert.Equal(1, DrawnOpacity(In<Panel>(tiles[6], "Indicator")));
        Assert.True(DrawnOpacity(In<Border>(tiles[0], "Grip")) > 0);   // the grip's own style is subtle
        var tabs = In<ItemsControl>(s.Page, "ModeTabs");
        Assert.Equal(1, DrawnOpacity(tabs));
        Assert.True(tabs.IsHitTestVisible);
        Assert.True(In<Button>(s.Page, "AddModeButton").IsEffectivelyEnabled);
    }

    /// <summary>The controller's panel (the device card) sits over the selected tile's inspector,
    /// and the side panel keeps its size.</summary>
    [AvaloniaFact]
    public void SidePanelShowsTheControllersPanelOverTheInspector_AtTheSameSize()
    {
        using var s = Show(5, IndicatorKind.Led, IndicatorKind.LedPair, IndicatorKind.Screen, IndicatorKind.None);
        var inspector = s.Page.FindControl<Border>("Inspector")!;
        var size = inspector.Bounds.Size;
        var panel = new TextBlock { Name = "HardwarePanelProbe", Text = "Controller side" };
        s.Page.HardwareSidePanel = panel;
        Settle(s.Window);

        Assert.Same(panel, In<ContentControl>(s.Page, "SidePanelHost").Content);
        Assert.True(panel.IsEffectivelyVisible);
        Assert.Equal(size, inspector.Bounds.Size);
        var content = s.Page.FindControl<ContentControl>("InspectorContent")!;
        Assert.True(content.IsEffectivelyVisible);
        Assert.True(content.TranslatePoint(new Point(0, 0), s.Page)!.Value.Y
                    >= panel.TranslatePoint(new Point(0, panel.Bounds.Height), s.Page)!.Value.Y);
    }

    [AvaloniaFact]
    public void UnwiredTiles_ShowNoWiringParts_AndKeepTheirControlsSize()
    {
        using var s = Show(false, 5, IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.Led);
        foreach (var tile in Tiles(s.Page))
        {
            Assert.All(tile.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Rectangle>()
                           .Where(r => r.Classes.Contains("ProblemOutline")), r => Assert.False(r.IsVisible));
            Assert.All(tile.GetVisualDescendants().OfType<Control>().Where(c => c.Name == "WiringRow"),
                       c => Assert.False(c.IsVisible));
        }
        foreach (var tile in Tiles(s.Page).OfType<ButtonTile>())
        {
            // Row 0 is the LED's height alone: the cap (top margin 7) starts where the LED ends.
            var led = In<Avalonia.Controls.Shapes.Ellipse>(tile, "Led");
            var ledBottom = led.TranslatePoint(new Point(0, led.Bounds.Height), tile)!.Value.Y;
            var capTop = In<Avalonia.Controls.Shapes.Ellipse>(tile, "Cap").TranslatePoint(new Point(0, 0), tile)!.Value.Y;
            Assert.Equal(ledBottom + 7, capTop, 0.5);
        }
    }

    /// <summary>A control with a wiring problem keeps the danger outline on the drawing
    /// (controller-page spec §4.1).</summary>
    [AvaloniaFact]
    public void AWiringProblemShowsTheOutline()
    {
        using var s = Show(5, IndicatorKind.Led, IndicatorKind.LedPair, IndicatorKind.Screen, IndicatorKind.None);
        var tile = Tiles(s.Page)[1];
        var outline = tile.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Rectangle>()
            .Single(r => r.Classes.Contains("ProblemOutline"));
        Assert.False(outline.IsVisible);

        s.Vm.Sliders.Items[1].Wiring!.Problem = "Slider 2 also uses GPIO 33.";
        Settle(s.Window);
        Assert.True(outline.IsVisible);
        Assert.Equal(1, DrawnOpacity(outline));
    }
}
