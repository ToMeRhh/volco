using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.ViewModels;

namespace Volco.App.Views;

/// <summary>
/// Drag-to-reorder for the Controls page's mode tabs, only while the strip is in edit mode
/// (spec controls-page §9.4). A tab's position is the mode's: the first is the start-up mode, and
/// colours follow positions.
/// <list type="bullet">
/// <item>A press on a tab followed by more than <see cref="DragThreshold"/> px of movement picks
/// it up; it follows the pointer. A press without movement stays a click (select, rename).</item>
/// <item>The drop index is the tab whose centre is nearest the pointer: the nearest row first,
/// then the nearest along it. Release commits (<see cref="ButtonsViewModel.MoveMode"/>); Escape
/// or a lost capture puts the tab back.</item>
/// <item>Alt+Left / Alt+Right on a focused tab moves it one place, keeping focus on it.</item>
/// </list>
/// </summary>
internal sealed class ModeTabReorder
{
    private const double DragThreshold = 4;

    private readonly ItemsControl _tabs;
    private readonly Func<ButtonsViewModel?> _model;
    private Press? _press;
    private DragState? _drag;

    /// <summary>A left press on a tab, not yet a drag.</summary>
    private sealed record Press(IPointer Pointer, Control Container, int From, Point Start);

    private sealed class DragState
    {
        public required ButtonsViewModel Model { get; init; }
        public required IPointer Pointer { get; init; }
        public required Control Container { get; init; }
        public required List<Point> Centers { get; init; }
        public required int From { get; init; }
        public required Point Start { get; init; }
        public int Target { get; set; }
    }

    private ModeTabReorder(ItemsControl tabs, Func<ButtonsViewModel?> model)
    {
        _tabs = tabs;
        _model = model;
        tabs.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        tabs.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        // Tunnel, so a drop is handled before the tab's own release turns it into a click.
        tabs.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        tabs.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>Enables reordering on the mode tab strip <paramref name="tabs"/>.</summary>
    public static ModeTabReorder Attach(ItemsControl tabs, Func<ButtonsViewModel?> model) => new(tabs, model);

    /// <summary>True while a tab is picked up.</summary>
    public bool IsDragging => _drag is not null;

    // ── Pointer ───────────────────────────────────────────────────────────────

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        _press = null;
        if (_drag is not null || _model() is not { IsEditingModes: true }) return;
        if (!e.GetCurrentPoint(_tabs).Properties.IsLeftButtonPressed) return;
        if (TabSource(e.Source) is not { } container) return;
        var from = _tabs.IndexFromContainer(container);
        if (from < 0) return;
        _press = new Press(e.Pointer, container, from, e.GetPosition(_tabs));
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_drag is { } drag)
        {
            if (e.Pointer != drag.Pointer) return;
            Follow(drag, e.GetPosition(_tabs));
            e.Handled = true;
            return;
        }
        if (_press is not { } press || e.Pointer != press.Pointer) return;
        var at = e.GetPosition(_tabs);
        var delta = at - press.Start;
        if (Math.Abs(delta.X) <= DragThreshold && Math.Abs(delta.Y) <= DragThreshold) return;
        _press = null;
        if (_model() is not { IsEditingModes: true } model) return;
        Begin(model, press);
        if (_drag is { } started) Follow(started, at);
        e.Handled = true;
    }

    private void Begin(ButtonsViewModel model, Press press)
    {
        var centers = new List<Point>();
        for (int i = 0; i < model.Modes.Count; i++)
        {
            var c = _tabs.ContainerFromIndex(i);
            var origin = c?.TranslatePoint(new Point(0, 0), _tabs);
            centers.Add(c is null || origin is null
                ? new Point(double.MaxValue, double.MaxValue)
                : new Point(origin.Value.X + c.Bounds.Width / 2, origin.Value.Y + c.Bounds.Height / 2));
        }
        _drag = new DragState
        {
            Model = model,
            Pointer = press.Pointer,
            Container = press.Container,
            Centers = centers,
            From = press.From,
            Target = press.From,
            Start = press.Start,
        };
        press.Container.ZIndex = 1;
        press.Container.Classes.Add("dragging");
        press.Container.PointerCaptureLost += OnCaptureLost;
        press.Pointer.Capture(press.Container);
    }

    private void Follow(DragState drag, Point at)
    {
        var delta = at - drag.Start;
        drag.Container.RenderTransform = new TranslateTransform(delta.X, delta.Y);
        drag.Target = NearestIndex(drag.Centers, at);
    }

    /// <summary>The tab whose centre is nearest <paramref name="at"/>: the nearest row, then the
    /// nearest along it.</summary>
    internal static int NearestIndex(IReadOnlyList<Point> centers, Point at)
    {
        int best = -1;
        double bestDy = double.MaxValue, bestDx = double.MaxValue;
        for (int i = 0; i < centers.Count; i++)
        {
            var dy = Math.Abs(centers[i].Y - at.Y);
            var dx = Math.Abs(centers[i].X - at.X);
            // Tabs in one row share a centre line (to within a pixel).
            if (dy < bestDy - 0.5 || (Math.Abs(dy - bestDy) <= 0.5 && dx < bestDx))
            {
                best = i;
                bestDy = dy;
                bestDx = dx;
            }
        }
        return best;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_press is { } press && e.Pointer == press.Pointer) _press = null;
        if (_drag is { } drag && e.Pointer == drag.Pointer)
        {
            Finish(commit: true);
            e.Handled = true;   // a drop is not a click
        }
    }

    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_drag is { } drag && e.Pointer == drag.Pointer) Finish(commit: false);
    }

    private void Finish(bool commit)
    {
        var drag = _drag;
        if (drag is null) return;
        _drag = null;   // first: releasing capture below raises CaptureLost again

        drag.Container.PointerCaptureLost -= OnCaptureLost;
        drag.Container.RenderTransform = null;
        drag.Container.ZIndex = 0;
        drag.Container.Classes.Remove("dragging");
        drag.Pointer.Capture(null);

        if (commit && drag.Target >= 0 && drag.Target != drag.From)
        {
            drag.Model.MoveMode(drag.From, drag.Target);
            FocusLater(drag.Target);
        }
    }

    // ── Keyboard ──────────────────────────────────────────────────────────────

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _drag is not null)
        {
            Finish(commit: false);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers != KeyModifiers.Alt || e.Key is not (Key.Left or Key.Right)) return;
        if (_model() is not { IsEditingModes: true } model) return;
        if (e.Source is not RadioButton { Name: "ModeTab" } tab || ContainerOf(tab) is not { } container) return;

        var from = _tabs.IndexFromContainer(container);
        var to = e.Key == Key.Left ? from - 1 : from + 1;
        e.Handled = true;
        if (from < 0 || to < 0 || to >= model.Modes.Count) return;
        model.MoveMode(from, to);
        FocusLater(to);
    }

    /// <summary>Focuses the tab at <paramref name="index"/> once the rebuilt strip is laid out.</summary>
    private void FocusLater(int index)
        => Dispatcher.UIThread.Post(() => FocusTab(index), DispatcherPriority.Loaded);

    internal void FocusTab(int index)
    {
        _tabs.UpdateLayout();
        TabAt(_tabs, index)?.Focus(NavigationMethod.Tab);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>The tab (RadioButton) at <paramref name="index"/> of <paramref name="tabs"/>.</summary>
    internal static RadioButton? TabAt(ItemsControl tabs, int index)
        => tabs.ContainerFromIndex(index)?.GetVisualDescendants().OfType<RadioButton>().FirstOrDefault();

    /// <summary>The container of the tab a press started on, or null when it started on the
    /// tab's rename box or ×, which keep their own pointer handling.</summary>
    private Control? TabSource(object? source)
    {
        for (var v = source as Visual; v is not null; v = v.GetVisualParent())
        {
            if (v is TextBox) return null;
            if (v is Button and not RadioButton) return null;
            if (v is RadioButton { Name: "ModeTab" } tab) return ContainerOf(tab);
        }
        return null;
    }

    /// <summary>The strip's item container (a direct child of its items panel) holding <paramref name="visual"/>.</summary>
    private Control? ContainerOf(Visual visual)
    {
        var panel = _tabs.ItemsPanelRoot;
        if (panel is null) return null;
        for (Visual? v = visual; v is not null; v = v.GetVisualParent())
            if (v.GetVisualParent() == panel) return v as Control;
        return null;
    }
}
