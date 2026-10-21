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

/// <summary>Spec docs/design/hardware-page.md §3.3: Controls and Hardware decorate one stage, so
/// switching between them never moves or resizes a tile.</summary>
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

    private static void SetMode(Shown s, StageMode mode)
    {
        s.Page.Mode = mode;
        Settle(s.Window);
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

    private static void AssertSameRects(List<Rect> expected, List<Rect> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].X, actual[i].X, 0.5);
            Assert.Equal(expected[i].Y, actual[i].Y, 0.5);
            Assert.Equal(expected[i].Width, actual[i].Width, 0.5);
            Assert.Equal(expected[i].Height, actual[i].Height, 0.5);
        }
    }

    [AvaloniaFact]
    public void TileBoundsAreEqualInBothModes()
    {
        using var s = Show(5, IndicatorKind.Led, IndicatorKind.LedPair, IndicatorKind.Screen, IndicatorKind.None);
        Assert.Equal(9, Tiles(s.Page).Count);
        Assert.True(In<TextBlock>(Tiles(s.Page)[5], "MainAction").TextLayout.TextLines.Count >= 2);
        var controls = TileRects(s);

        SetMode(s, StageMode.Hardware);
        AssertSameRects(controls, TileRects(s));

        SetMode(s, StageMode.Controls);
        AssertSameRects(controls, TileRects(s));
    }

    [AvaloniaFact]
    public void TileBoundsAreEqualOnADenseBoard()
    {
        var kinds = Enumerable.Range(0, 10).Select(i => (IndicatorKind)(i % 4)).ToArray();
        using var s = Show(10, kinds);
        Assert.True(s.Vm.Controls.IsDense);
        Assert.Equal(20, Tiles(s.Page).Count);
        var controls = TileRects(s);

        SetMode(s, StageMode.Hardware);
        AssertSameRects(controls, TileRects(s));
    }

    [AvaloniaFact]
    public void TitleRowHeightIsTheSame()
    {
        using var s = Show(5, IndicatorKind.Led, IndicatorKind.LedPair, IndicatorKind.Screen, IndicatorKind.None);
        var row = In<Grid>(s.Page, "TitleRow");
        Assert.Equal("Controls", In<TextBlock>(row, "PageTitle").Text);
        var height = row.Bounds.Height;
        Assert.Equal(32, height, 0.5);

        SetMode(s, StageMode.Hardware);
        Assert.Equal("Hardware", In<TextBlock>(row, "PageTitle").Text);
        Assert.Equal(height, row.Bounds.Height, 0.5);
        Assert.Contains("hardware", s.Page.Classes);
    }

    [AvaloniaFact]
    public void HardwareModeShowsWiringCaptions()
    {
        using var s = Show(5, IndicatorKind.Led, IndicatorKind.LedPair, IndicatorKind.Screen, IndicatorKind.None);
        var tiles = Tiles(s.Page);
        var pin = tiles[0].GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "GPIO 33");
        var main = In<TextBlock>(tiles[5], "MainAction");
        var indicator = tiles[6].GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "18 · 19");
        Assert.Equal(0, DrawnOpacity(pin));
        Assert.Equal(1, DrawnOpacity(main));

        SetMode(s, StageMode.Hardware);
        Assert.Equal(1, DrawnOpacity(pin));
        Assert.Equal(1, DrawnOpacity(indicator));
        Assert.Equal(0, DrawnOpacity(main));
        Assert.Equal(0, DrawnOpacity(In<Panel>(tiles[6], "Indicator")));
        Assert.Equal(0, DrawnOpacity(In<Border>(tiles[0], "Grip")));
    }

    [AvaloniaFact]
    public void ModeTabsKeepTheirSpaceInHardware()
    {
        using var s = Show(5, IndicatorKind.Led, IndicatorKind.LedPair, IndicatorKind.Screen, IndicatorKind.None);
        var tabs = In<ItemsControl>(s.Page, "ModeTabs");
        var height = tabs.Bounds.Height;
        Assert.True(height > 0);
        Assert.True(tabs.IsHitTestVisible);

        SetMode(s, StageMode.Hardware);
        Assert.Equal(height, tabs.Bounds.Height, 0.5);
        Assert.False(tabs.IsHitTestVisible);
        Assert.Equal(0, DrawnOpacity(tabs));
        Assert.Equal(0, DrawnOpacity(In<Border>(s.Page, "ModeFrameTint")));
    }

    [AvaloniaFact]
    public void SidePanelShowsTheHardwarePanel_AtTheSameSize()
    {
        using var s = Show(5, IndicatorKind.Led, IndicatorKind.LedPair, IndicatorKind.Screen, IndicatorKind.None);
        var inspector = s.Page.FindControl<Border>("Inspector")!;
        var size = inspector.Bounds.Size;
        var panel = new TextBlock { Name = "HardwarePanelProbe", Text = "Hardware side" };
        s.Page.HardwareSidePanel = panel;

        SetMode(s, StageMode.Hardware);
        Assert.Same(panel, In<TransitioningContentControl>(s.Page, "SidePanelHost").Content);
        Assert.True(panel.IsEffectivelyVisible);
        Assert.Equal(size, inspector.Bounds.Size);

        SetMode(s, StageMode.Controls);
        Assert.Same(s.Page.FindControl<ContentControl>("InspectorContent"),
                    In<TransitioningContentControl>(s.Page, "SidePanelHost").Content);
    }

    [AvaloniaFact]
    public void UnwiredTiles_ShowNoWiringParts_AndKeepTheirControlsSize()
    {
        using var s = Show(false, 5, IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.Led);
        foreach (var mode in new[] { StageMode.Controls, StageMode.Hardware })
        {
            SetMode(s, mode);
            foreach (var tile in Tiles(s.Page))
            {
                Assert.All(tile.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Rectangle>()
                               .Where(r => r.Classes.Contains("ProblemOutline")), r => Assert.False(r.IsVisible));
                Assert.All(tile.GetVisualDescendants().OfType<Control>().Where(c => c.Name == "WiringRow"),
                           c => Assert.False(c.IsVisible));
            }
            foreach (var tile in Tiles(s.Page).OfType<ButtonTile>())
            {
                Assert.False(In<TextBlock>(tile, "WiringIndicator").IsVisible);
                // Row 0 is the LED's height alone: the cap (top margin 7) starts where the LED ends.
                var led = In<Avalonia.Controls.Shapes.Ellipse>(tile, "Led");
                var ledBottom = led.TranslatePoint(new Point(0, led.Bounds.Height), tile)!.Value.Y;
                var capTop = In<Avalonia.Controls.Shapes.Ellipse>(tile, "Cap").TranslatePoint(new Point(0, 0), tile)!.Value.Y;
                Assert.Equal(ledBottom + 7, capTop, 0.5);
            }
        }
    }

    [AvaloniaFact]
    public void ModeButtonsAreOutOfKeyboardReachInHardware()
    {
        using var s = Show(5, IndicatorKind.Led, IndicatorKind.LedPair, IndicatorKind.Screen, IndicatorKind.None);
        var add = In<Button>(s.Page, "AddModeButton");
        var modes = s.Vm.Controls.Buttons.Modes.Count;
        Assert.True(add.IsEffectivelyEnabled);

        SetMode(s, StageMode.Hardware);
        foreach (var name in new[] { "AddModeButton", "MakeLiveButton", "EditModesButton" })
            Assert.False(In<Button>(s.Page, name).IsEffectivelyEnabled, name);
        Assert.All(s.Page.GetVisualDescendants().OfType<RadioButton>().Where(r => r.Name == "ModeTab"),
                   tab => Assert.False(tab.IsEffectivelyEnabled));
        Assert.False(add.Focus(Avalonia.Input.NavigationMethod.Tab));
        add.Focus(Avalonia.Input.NavigationMethod.Tab);
        Settle(s.Window);
        Assert.False(add.IsFocused);
        Assert.Equal(modes, s.Vm.Controls.Buttons.Modes.Count);

        SetMode(s, StageMode.Controls);
        Assert.True(add.IsEffectivelyEnabled);
    }
}
