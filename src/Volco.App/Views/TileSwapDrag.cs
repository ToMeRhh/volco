using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using Volco.App.Controls;
using Volco.App.ViewModels;

namespace Volco.App.Views;

/// <summary>
/// Drag to swap on the Controls page's drawing (spec controls-page §2.9–10, §11).
/// <list type="bullet">
/// <item>A left press on a tile's grip followed by more than <see cref="DragThreshold"/> px of
/// movement picks it up: a button's action rows, or a whole slider. Missing tiles have no grip.</item>
/// <item>The drag image is a <see cref="VisualBrush"/> of that source in the window's adorner
/// layer, following the pointer; the source tile is dimmed (class <c>dragSource</c>), and the tile
/// of the same kind under the pointer that isn't missing or the source is marked
/// (<c>dropTarget</c>).</item>
/// <item>Release over a marked tile swaps (<see cref="ControlsViewModel.Swap"/>); release anywhere
/// else, Escape or a lost capture changes nothing.</item>
/// </list>
/// </summary>
internal sealed class TileSwapDrag
{
    private const double DragThreshold = 4;

    private readonly Control _drawing;
    private readonly Func<ControlsViewModel?> _model;
    private Press? _press;
    private DragState? _drag;

    /// <summary>A left press on a grip, not yet a drag. <paramref name="Offset"/> is the press
    /// point within the drag image's source.</summary>
    private sealed record Press(IPointer Pointer, Control Tile, Visual Source, Point Start, Point Offset,
                                InputElement? Captor);

    private sealed class DragState
    {
        public required ControlsViewModel Model { get; init; }
        public required IPointer Pointer { get; init; }
        public required Control Tile { get; init; }
        public required Point Offset { get; init; }
        public AdornerLayer? Layer { get; init; }
        public Border? Image { get; init; }
        public Control? Target { get; set; }
    }

    private TileSwapDrag(Control drawing, Func<ControlsViewModel?> model)
    {
        _drawing = drawing;
        _model = model;
        drawing.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        drawing.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        drawing.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        drawing.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>Enables drag to swap on the tiles inside <paramref name="drawing"/>.</summary>
    public static TileSwapDrag Attach(Control drawing, Func<ControlsViewModel?> model) => new(drawing, model);

    /// <summary>True while a tile is picked up.</summary>
    public bool IsDragging => _drag is not null;

    // ── Pointer ───────────────────────────────────────────────────────────────

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        SetPress(null);
        if (_drag is not null || _model() is null) return;
        if (!e.GetCurrentPoint(_drawing).Properties.IsLeftButtonPressed) return;
        if (GripTile(e.Source) is not { } tile || IsMissing(tile)) return;
        if (ImageSource(tile) is not { } source) return;
        SetPress(new Press(e.Pointer, tile, source, e.GetPosition(_drawing), e.GetPosition(source),
                           e.Source as InputElement));
    }

    /// <summary>Replaces the pending press; a press ends when the element the press captured loses
    /// the pointer.</summary>
    private void SetPress(Press? press)
    {
        if (_press?.Captor is { } old) old.PointerCaptureLost -= OnPressCaptureLost;
        _press = press;
        if (press?.Captor is { } captor) captor.PointerCaptureLost += OnPressCaptureLost;
    }

    private void OnPressCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_press is { } press && e.Pointer == press.Pointer) SetPress(null);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_drag is { } drag)
        {
            if (e.Pointer != drag.Pointer) return;
            Follow(drag, e);
            e.Handled = true;
            return;
        }
        if (_press is not { } press || e.Pointer != press.Pointer) return;
        // The button went up unseen (e.g. capture moved away): no drag from a stale press.
        if (!e.GetCurrentPoint(_drawing).Properties.IsLeftButtonPressed)
        {
            SetPress(null);
            return;
        }
        var delta = e.GetPosition(_drawing) - press.Start;
        if (Math.Abs(delta.X) <= DragThreshold && Math.Abs(delta.Y) <= DragThreshold) return;
        SetPress(null);
        if (_model() is not { } model) return;
        Begin(model, press);
        if (_drag is { } started) Follow(started, e);
        e.Handled = true;
    }

    private void Begin(ControlsViewModel model, Press press)
    {
        var layer = AdornerLayer.GetAdornerLayer(_drawing);
        Border? image = null;
        if (layer is not null)
        {
            image = new Border
            {
                Classes = { "SwapDragImage" },
                Width = press.Source.Bounds.Width,
                Height = press.Source.Bounds.Height,
                Background = new VisualBrush(press.Source)
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top,
                },
            };
            layer.Children.Add(image);
        }
        _drag = new DragState
        {
            Model = model,
            Pointer = press.Pointer,
            Tile = press.Tile,
            Offset = press.Offset,
            Layer = layer,
            Image = image,
        };
        press.Tile.Classes.Add("dragSource");
        press.Tile.PointerCaptureLost += OnCaptureLost;
        press.Pointer.Capture(press.Tile);
    }

    private void Follow(DragState drag, PointerEventArgs e)
    {
        if (drag.Layer is { } layer && drag.Image is { } image)
        {
            var at = e.GetPosition(layer);
            Canvas.SetLeft(image, at.X - drag.Offset.X);
            Canvas.SetTop(image, at.Y - drag.Offset.Y);
        }
        var target = TargetAt(drag.Tile, e.GetPosition(_drawing));
        if (ReferenceEquals(target, drag.Target)) return;
        drag.Target?.Classes.Remove("dropTarget");
        drag.Target = target;
        target?.Classes.Add("dropTarget");
    }

    /// <summary>The tile of <paramref name="source"/>'s kind at <paramref name="at"/> that it
    /// could swap with, or null.</summary>
    private Control? TargetAt(Control source, Point at)
    {
        foreach (var tile in _drawing.GetVisualDescendants().OfType<Control>())
        {
            if (tile.GetType() != source.GetType() || ReferenceEquals(tile, source) || IsMissing(tile)) continue;
            if (tile.TranslatePoint(new Point(0, 0), _drawing) is not { } origin) continue;
            if (new Rect(origin, tile.Bounds.Size).Contains(at)) return tile;
        }
        return null;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_press is { } press && e.Pointer == press.Pointer) SetPress(null);
        if (_drag is { } drag && e.Pointer == drag.Pointer)
        {
            Follow(drag, e);
            Finish(commit: true);
            e.Handled = true;
        }
    }

    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_drag is { } drag && e.Pointer == drag.Pointer) Finish(commit: false);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _drag is null) return;
        Finish(commit: false);
        e.Handled = true;
    }

    private void Finish(bool commit)
    {
        var drag = _drag;
        if (drag is null) return;
        _drag = null;   // first: releasing capture below raises CaptureLost again

        drag.Tile.PointerCaptureLost -= OnCaptureLost;
        drag.Tile.Classes.Remove("dragSource");
        drag.Target?.Classes.Remove("dropTarget");
        if (drag.Image is not null) drag.Layer?.Children.Remove(drag.Image);
        drag.Pointer.Capture(null);

        // A swap rebuilds slider tiles, so the classes are off before it.
        if (commit && drag.Target?.DataContext is ObservableObject target && drag.Tile.DataContext is ObservableObject source)
            drag.Model.Swap(source, target);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsMissing(Control tile)
        => tile.DataContext is SliderItemViewModel { IsMissing: true } or ButtonItemViewModel { IsMissing: true };

    /// <summary>The tile whose grip a press started on, or null.</summary>
    private static Control? GripTile(object? source)
    {
        bool onGrip = false;
        for (var v = source as Visual; v is not null; v = v.GetVisualParent())
        {
            if (v is Border { Name: "Grip" }) onGrip = true;
            if (v is SliderTile or ButtonTile) return onGrip ? (Control)v : null;
        }
        return null;
    }

    /// <summary>What the drag carries: a button's action rows, or the whole slider tile (its root
    /// panel, so the tile's own dimming stays out of the image).</summary>
    private static Visual? ImageSource(Control tile) => tile switch
    {
        ButtonTile => tile.GetVisualDescendants().OfType<Grid>().FirstOrDefault(g => g.Name == "ActionRows"),
        SliderTile => tile.GetVisualDescendants().OfType<Panel>().FirstOrDefault(p => p.Classes.Contains("TileRoot")),
        _ => null,
    };
}
