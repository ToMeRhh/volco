using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.Controls;
using Volco.App.ViewModels;
using Volco.App.Views.Pages;
using Volco.Core.Config;
using Volco.Core.Input.Actions;
using Volco.Core.Communication;
using Xunit;

namespace Volco.App.Tests.Pages;

/// <summary>Spec docs/design/controls-page.md §2.9–10 and §11: drag a tile's grip onto another
/// tile of its kind to swap, and the tile menu's "Swap with".</summary>
public class SwapDragTests
{
    private sealed record Shown(ControlsPage Page, Window Window, TestRig Rig, SettingsViewModel Vm) : IDisposable
    {
        public AppConfig Config => Rig.Config.Config;

        public void Dispose()
        {
            Window.Close();
            Vm.Dispose();
            Rig.Dispose();
        }
    }

    private static void Hardware(AppConfig c, int sliders, params IndicatorKind[] buttons)
        => c.Controller = new ControllerInfoConfig { ProtocolVersion = 1, Sliders = sliders, Buttons = buttons.ToList() };

    /// <summary>Five sliders named S1…S5 and three buttons: 1 mutes the output, 2 is unset, 3
    /// plays/pauses (in Default), on a controller that has all of them unless
    /// <paramref name="configure"/> says otherwise.</summary>
    private static Shown Show(Action<AppConfig>? configure = null)
    {
        var rig = new TestRig();
        var c = rig.Config.Config;
        Hardware(c, 5, IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.Led);
        for (int i = 0; i < c.Sliders.Count; i++) c.Sliders[i].Name = $"S{i + 1}";
        c.Buttons[0].Default().SinglePress.Action = ButtonActionType.MuteCurrentOutput;
        c.Buttons[2].Default().SinglePress.Action = ButtonActionType.MediaPlayPause;
        configure?.Invoke(c);
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new ControlsPage(vm.Controls);
        var window = new Window { Content = page, Width = 932, Height = 680 };
        window.Show();
        Settle(window);
        return new Shown(page, window, rig, vm);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static T In<T>(Visual root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static List<ButtonTile> ButtonTiles(Shown s) => s.Page.GetVisualDescendants().OfType<ButtonTile>().ToList();
    private static List<SliderTile> SliderTiles(Shown s) => s.Page.GetVisualDescendants().OfType<SliderTile>().ToList();

    private static Border Grip(Control tile) => In<Border>(tile, "Grip");

    private static Point Centre(Control control)
        => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2),
                                  TopLevel.GetTopLevel(control)!)!.Value;

    private static string Snapshot(Shown s) => JsonSerializer.Serialize(s.Config, ConfigJsonContext.Default.AppConfig);

    /// <summary>Presses the grip's centre and moves in steps to <paramref name="end"/>, without
    /// releasing.</summary>
    private static void PickUp(Shown s, Control grip, Point end, int steps = 6)
    {
        var start = Centre(grip);
        s.Window.MouseDown(start, MouseButton.Left);
        Settle(s.Window);
        for (int i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            s.Window.MouseMove(new Point(start.X + (end.X - start.X) * t, start.Y + (end.Y - start.Y) * t),
                               RawInputModifiers.LeftMouseButton);
        }
        Settle(s.Window);
    }

    private static void Drop(Shown s, Point end)
    {
        s.Window.MouseUp(end, MouseButton.Left);
        Settle(s.Window);
    }

    private static void Drag(Shown s, Control grip, Point end)
    {
        PickUp(s, grip, end);
        Drop(s, end);
    }

    private static List<Border> DragImages(Shown s)
        => AdornerLayer.GetAdornerLayer(s.Page)!.Children.OfType<Border>().Where(b => b.Classes.Contains("SwapDragImage")).ToList();

    private static void AssertCleared(Shown s)
    {
        Assert.False(s.Page.SwapDrag.IsDragging);
        Assert.Empty(DragImages(s));
        foreach (var tile in ButtonTiles(s).Cast<Control>().Concat(SliderTiles(s)))
        {
            Assert.DoesNotContain("dragSource", tile.Classes);
            Assert.DoesNotContain("dropTarget", tile.Classes);
        }
    }

    // ── Drag ──────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void DragButtonGrip_OntoButton3_SwapsEditedModeActions()
    {
        using var s = Show(c =>
        {
            c.Modes.Add(new ModeConfig { Id = "gaming", Name = "Gaming" });
            c.Buttons[0].GetOrAddBinding("gaming").SinglePress.Action = ButtonActionType.MuteCurrentInput;
            c.Buttons[2].GetOrAddBinding("gaming").SinglePress.Action = ButtonActionType.NextMode;
        });
        s.Vm.Buttons.Modes.Single(p => p.Id == "gaming").SelectCommand.Execute(null);
        Settle(s.Window);
        var tiles = ButtonTiles(s);

        Drag(s, Grip(tiles[0]), Centre(tiles[2]));

        Assert.Equal(ButtonActionType.NextMode, s.Config.Buttons[0].BindingFor("gaming").SinglePress.Action);
        Assert.Equal(ButtonActionType.MuteCurrentInput, s.Config.Buttons[2].BindingFor("gaming").SinglePress.Action);
        // Only the edited mode moves.
        Assert.Equal(ButtonActionType.MuteCurrentOutput, s.Config.Buttons[0].Default().SinglePress.Action);
        Assert.Equal(ButtonActionType.MediaPlayPause, s.Config.Buttons[2].Default().SinglePress.Action);
        Assert.Same(s.Vm.Buttons.Items[2], s.Vm.Controls.Selected);
        AssertCleared(s);
    }

    [AvaloniaFact]
    public void MoveWithoutHeldButton_AfterPress_DoesNotStartDrag()
    {
        using var s = Show();
        var tiles = ButtonTiles(s);
        var before = Snapshot(s);
        var start = Centre(Grip(tiles[0]));
        var end = Centre(tiles[2]);

        s.Window.MouseDown(start, MouseButton.Left);
        Settle(s.Window);
        // The button went up unseen: moves past the threshold with no button held.
        s.Window.MouseMove(new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2), RawInputModifiers.None);
        s.Window.MouseMove(end, RawInputModifiers.None);
        Settle(s.Window);

        Assert.False(s.Page.SwapDrag.IsDragging);
        Assert.DoesNotContain("dragSource", tiles[0].Classes);
        AssertCleared(s);

        s.Window.MouseUp(end, MouseButton.Left);
        Settle(s.Window);
        Assert.Equal(before, Snapshot(s));
    }

    [AvaloniaFact]
    public void DragSliderGrip_OntoSlider2_SwapsSliders()
    {
        using var s = Show();
        var tiles = SliderTiles(s);

        Drag(s, Grip(tiles[0]), Centre(tiles[1]));

        Assert.Equal(new[] { "S2", "S1", "S3", "S4", "S5" }, s.Config.Sliders.Select(x => x.Name));
        Assert.Same(s.Vm.Sliders.Items[1], s.Vm.Controls.Selected);
        AssertCleared(s);
    }

    [AvaloniaFact]
    public void DuringDrag_SourceDimmed_TargetMarked_AdornerShown()
    {
        using var s = Show();
        var tiles = ButtonTiles(s);
        var grip = Grip(tiles[0]);
        var rows = In<Grid>(tiles[0], "ActionRows");
        var pressInRows = new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2);
        pressInRows = grip.TranslatePoint(pressInRows, rows)!.Value;
        var end = Centre(tiles[2]);

        PickUp(s, grip, end);

        Assert.True(s.Page.SwapDrag.IsDragging);
        Assert.Contains("dragSource", tiles[0].Classes);
        Assert.Equal(0.35, tiles[0].Opacity, 3);
        Assert.Contains("dropTarget", tiles[2].Classes);
        Assert.DoesNotContain("dropTarget", tiles[1].Classes);
        var outline = tiles[2].GetVisualDescendants().OfType<Rectangle>().Single(r => r.Classes.Contains("DropOutline"));
        Assert.True(outline.IsVisible);
        Assert.NotNull(outline.StrokeDashArray);
        Assert.False(tiles[1].GetVisualDescendants().OfType<Rectangle>().Single(r => r.Classes.Contains("DropOutline")).IsVisible);

        var image = Assert.Single(DragImages(s));
        Assert.Equal(0.85, image.Opacity, 3);
        var brush = Assert.IsType<VisualBrush>(image.Background);
        Assert.Same(rows, brush.Visual);
        Assert.Equal(rows.Bounds.Size, new Size(image.Width, image.Height));
        // It follows the pointer, offset by the press point within the action rows.
        var layer = AdornerLayer.GetAdornerLayer(s.Page)!;
        var endInLayer = s.Window.TranslatePoint(end, layer)!.Value;
        Assert.Equal(endInLayer.X - pressInRows.X, Canvas.GetLeft(image), 1);
        Assert.Equal(endInLayer.Y - pressInRows.Y, Canvas.GetTop(image), 1);

        Drop(s, end);
        AssertCleared(s);
        Assert.Equal(1, tiles[0].Opacity, 3);
    }

    [AvaloniaFact]
    public void DuringSliderDrag_ImageIsTheLabelRow()
    {
        using var s = Show();
        var tiles = SliderTiles(s);
        var end = Centre(tiles[3]);

        PickUp(s, Grip(tiles[0]), end);

        // What a swap moves: the name (else what it controls), not the fader and its level.
        var brush = Assert.IsType<VisualBrush>(Assert.Single(DragImages(s)).Background);
        var row = Assert.IsType<Grid>(brush.Visual);
        Assert.Equal("LabelRow", row.Name);
        Assert.Same(tiles[0], row.GetVisualAncestors().OfType<SliderTile>().First());
        Assert.Contains("dropTarget", tiles[3].Classes);

        Drop(s, end);
        Assert.Equal(new[] { "S4", "S2", "S3", "S1", "S5" }, s.Config.Sliders.Select(x => x.Name));
        AssertCleared(s);
    }

    [AvaloniaFact]
    public void Drop_OnSelf_Outside_OtherKind_Missing_DoesNothing()
    {
        using var s = Show(c => Hardware(c, 5, IndicatorKind.Led, IndicatorKind.Led));
        var before = Snapshot(s);
        var tiles = ButtonTiles(s);
        Assert.True(s.Vm.Buttons.Items[2].IsMissing);

        // On itself
        Drag(s, Grip(tiles[0]), Centre(In<Ellipse>(tiles[0], "Cap")));
        Assert.Equal(before, Snapshot(s));
        AssertCleared(s);

        // Outside the drawing (over the inspector)
        Drag(s, Grip(tiles[0]), Centre(s.Page.FindControl<Border>("Inspector")!));
        Assert.Equal(before, Snapshot(s));
        AssertCleared(s);

        // On a slider
        PickUp(s, Grip(tiles[0]), Centre(SliderTiles(s)[1]));
        Assert.All(SliderTiles(s), t => Assert.DoesNotContain("dropTarget", t.Classes));
        Drop(s, Centre(SliderTiles(s)[1]));
        Assert.Equal(before, Snapshot(s));
        AssertCleared(s);

        // On a missing button
        PickUp(s, Grip(tiles[0]), Centre(tiles[2]));
        Assert.DoesNotContain("dropTarget", tiles[2].Classes);
        Drop(s, Centre(tiles[2]));
        Assert.Equal(before, Snapshot(s));
        AssertCleared(s);
    }

    [AvaloniaFact]
    public void Escape_CancelsDrag()
    {
        using var s = Show();
        var before = Snapshot(s);
        var tiles = ButtonTiles(s);
        var end = Centre(tiles[2]);

        PickUp(s, Grip(tiles[0]), end);
        Assert.True(s.Page.SwapDrag.IsDragging);
        s.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        s.Window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Settle(s.Window);
        AssertCleared(s);

        Drop(s, end);
        Assert.Equal(before, Snapshot(s));
        AssertCleared(s);
    }

    [AvaloniaFact]
    public void PressWithoutMove_OnGrip_DoesNotSwap()
    {
        using var s = Show();
        var before = Snapshot(s);
        var grip = Grip(ButtonTiles(s)[0]);
        var at = Centre(grip);

        s.Window.MouseDown(at, MouseButton.Left);
        s.Window.MouseMove(new Point(at.X + 3, at.Y + 2), RawInputModifiers.LeftMouseButton);
        Settle(s.Window);
        Assert.False(s.Page.SwapDrag.IsDragging);
        s.Window.MouseUp(new Point(at.X + 3, at.Y + 2), MouseButton.Left);
        Settle(s.Window);

        Assert.Equal(before, Snapshot(s));
        AssertCleared(s);
        Assert.Same(s.Vm.Buttons.Items[0], s.Vm.Controls.Selected);   // a press still selects
    }

    [AvaloniaFact]
    public void MissingTile_HasNoGrip()
    {
        using var s = Show(c => Hardware(c, 4, IndicatorKind.Led, IndicatorKind.Led));
        var buttons = ButtonTiles(s);
        var sliders = SliderTiles(s);

        Assert.True(Grip(buttons[0]).IsVisible);
        Assert.False(Grip(buttons[2]).IsVisible);
        Assert.True(Grip(sliders[3]).IsVisible);
        Assert.False(Grip(sliders[4]).IsVisible);
        Assert.Equal("Drag onto another button to swap actions", ToolTip.GetTip(Grip(buttons[0])));
        Assert.Equal("Drag onto another slider to swap", ToolTip.GetTip(Grip(sliders[0])));
    }

    // ── Swap with ─────────────────────────────────────────────────────────────

    private static MenuItem OpenSwapWith(Shown s, Control tile)
    {
        tile.RaiseEvent(new ContextRequestedEventArgs());
        Settle(s.Window);
        var menu = tile.ContextMenu!;
        var swapWith = Assert.Single(menu.Items.OfType<MenuItem>());
        Assert.Equal("Swap with", swapWith.Header);
        return swapWith;
    }

    private static List<MenuItem> Entries(MenuItem swapWith) => swapWith.ItemsSource!.Cast<MenuItem>().ToList();

    [AvaloniaFact]
    public void ContextMenu_SwapWith_ListsAndSwaps()
    {
        using var s = Show(c => Hardware(c, 4, IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.Led));

        var button = ButtonTiles(s)[0];
        var swapWith = OpenSwapWith(s, button);
        Assert.True(button.ContextMenu!.IsOpen);
        Assert.Equal(new[] { "Button 2", "Button 3" }, Entries(swapWith).Select(e => (string)e.Header!));
        Entries(swapWith)[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        button.ContextMenu.Close();
        Settle(s.Window);
        Assert.Equal(ButtonActionType.MediaPlayPause, s.Config.Buttons[0].Default().SinglePress.Action);
        Assert.Equal(ButtonActionType.MuteCurrentOutput, s.Config.Buttons[2].Default().SinglePress.Action);

        // Sliders list the other sliders the controller has (slider 5 is missing).
        var slider = SliderTiles(s)[0];
        swapWith = OpenSwapWith(s, slider);
        Assert.Equal(new[] { "Slider 2", "Slider 3", "Slider 4" }, Entries(swapWith).Select(e => (string)e.Header!));
        Entries(swapWith)[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        slider.ContextMenu!.Close();
        Settle(s.Window);
        Assert.Equal(new[] { "S4", "S2", "S3", "S1", "S5" }, s.Config.Sliders.Select(x => x.Name));

        // A missing tile has nothing to swap with.
        var missing = SliderTiles(s)[4];
        swapWith = OpenSwapWith(s, missing);
        Assert.Empty(Entries(swapWith));
        Assert.False(swapWith.IsEnabled);
        missing.ContextMenu!.Close();
    }
}
