using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.ViewModels;

namespace Volco.App.Views;

/// <summary>
/// Drag-to-reorder for a card list whose order means something (position = hardware input).
/// <list type="bullet">
/// <item>Pressing a card's <c>Border.Grip</c> picks the card up; it follows the pointer and the
/// cards it passes slide out of the way. Nothing moves in the model until the drop, but the cards'
/// input numbers relabel live (<see cref="IReorderableList.PreviewMove"/>).</item>
/// <item>Releasing commits (<see cref="IReorderableList.Move"/>); Escape or a lost capture puts
/// everything back.</item>
/// <item>Alt+Up / Alt+Down on a focused grip moves that card one place, keeping focus on it.</item>
/// </list>
/// </summary>
internal sealed class CardReorder
{
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(180);

    private readonly ItemsControl _list;
    private readonly Func<IReorderableList?> _model;
    private DragState? _drag;

    private sealed class DragState
    {
        public required IReorderableList Model { get; init; }
        public required IPointer Pointer { get; init; }
        public required Control Container { get; init; }
        public required List<Control?> Containers { get; init; }
        public required List<double> Centers { get; init; }
        public required int From { get; init; }
        public required double StartY { get; init; }
        public required double Step { get; init; }
        public required double MinDy { get; init; }
        public required double MaxDy { get; init; }
        public int Target { get; set; }
    }

    private CardReorder(ItemsControl list, Func<IReorderableList?> model)
    {
        _list = list;
        _model = model;
        list.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        list.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        list.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        list.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
        list.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Enables reordering on <paramref name="list"/>, whose cards each contain a <c>Border.Grip</c>.</summary>
    public static CardReorder Attach(ItemsControl list, Func<IReorderableList?> model) => new(list, model);

    /// <summary>True while a card is picked up.</summary>
    public bool IsDragging => _drag is not null;

    // ── Pointer ───────────────────────────────────────────────────────────────

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_drag is not null || !e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed) return;
        if (GripOf(e.Source) is not { } grip || ContainerOf(grip) is not { } container) return;
        if (_model() is not { } model) return;

        var from = _list.IndexFromContainer(container);
        if (from < 0) return;

        var containers = new List<Control?>();
        var centers = new List<double>();
        for (int i = 0; i < model.Count; i++)
        {
            var c = _list.ContainerFromIndex(i);
            containers.Add(c);
            centers.Add(c is null ? 0 : c.Bounds.Y + c.Bounds.Height / 2);
        }

        var spacing = (_list.ItemsPanelRoot as StackPanel)?.Spacing ?? 0;
        var panelHeight = _list.ItemsPanelRoot?.Bounds.Height ?? container.Bounds.Bottom;
        _drag = new DragState
        {
            Model = model,
            Pointer = e.Pointer,
            Container = container,
            Containers = containers,
            Centers = centers,
            From = from,
            Target = from,
            StartY = e.GetPosition(_list).Y,
            Step = container.Bounds.Height + spacing,
            MinDy = -container.Bounds.Y,
            MaxDy = Math.Max(0, panelHeight - container.Bounds.Bottom),
        };

        foreach (var c in containers)
        {
            if (c is null || c == container) continue;
            c.Transitions = new Transitions
            {
                new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = SlideDuration },
            };
        }
        container.ZIndex = 1;
        container.Classes.Add("dragging");
        grip.Focus(NavigationMethod.Pointer);   // so Escape reaches us
        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        var drag = _drag;
        if (drag is null || e.Pointer != drag.Pointer) return;

        var dy = Math.Clamp(e.GetPosition(_list).Y - drag.StartY, drag.MinDy, drag.MaxDy);
        drag.Container.RenderTransform = TranslateY(dy);

        var center = drag.Centers[drag.From] + dy;
        var target = drag.From;
        for (int i = drag.From + 1; i < drag.Centers.Count; i++)
            if (center > drag.Centers[i]) target = i;
        for (int i = drag.From - 1; i >= 0; i--)
            if (center < drag.Centers[i]) target = i;

        if (target != drag.Target)
        {
            drag.Target = target;
            for (int i = 0; i < drag.Containers.Count; i++)
            {
                if (i == drag.From || drag.Containers[i] is not { } c) continue;
                var shift = drag.From < i && i <= target ? -drag.Step
                          : target <= i && i < drag.From ? drag.Step
                          : 0;
                c.RenderTransform = TranslateY(shift);
            }
            drag.Model.PreviewMove(drag.From, target);
        }
        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_drag is { } drag && e.Pointer == drag.Pointer)
        {
            Finish(commit: true);
            e.Handled = true;
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

        foreach (var c in drag.Containers)
        {
            if (c is null) continue;
            c.Transitions = null;
            c.RenderTransform = null;
        }
        drag.Container.ZIndex = 0;
        drag.Container.Classes.Remove("dragging");
        drag.Pointer.Capture(null);

        if (commit && drag.Target != drag.From) drag.Model.Move(drag.From, drag.Target);
        else drag.Model.CancelPreview();
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

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Alt) || e.Key is not (Key.Up or Key.Down)) return;
        if (GripOf(e.Source) is not { } grip || ContainerOf(grip) is not { } container) return;
        if (_model() is not { } model) return;

        var from = _list.IndexFromContainer(container);
        var to = e.Key == Key.Up ? from - 1 : from + 1;
        if (from < 0 || to < 0 || to >= model.Count) return;

        model.Move(from, to);
        e.Handled = true;
        Dispatcher.UIThread.Post(() => FocusGrip(to), DispatcherPriority.Loaded);
    }

    /// <summary>Focuses the grip of the card at <paramref name="index"/>.</summary>
    internal void FocusGrip(int index)
    {
        var container = _list.ContainerFromIndex(index);
        var grip = container?.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Classes.Contains("Grip"));
        grip?.Focus(NavigationMethod.Tab);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static TransformOperations TranslateY(double y)
    {
        var builder = TransformOperations.CreateBuilder(1);
        builder.AppendTranslate(0, y);
        return builder.Build();
    }

    private static Control? GripOf(object? source)
    {
        for (var v = source as Visual; v is not null; v = v.GetVisualParent())
            if (v is Control c && c.Classes.Contains("Grip")) return c;
        return null;
    }

    /// <summary>The list's item container (a direct child of its items panel) holding <paramref name="visual"/>.</summary>
    private Control? ContainerOf(Visual visual)
    {
        var panel = _list.ItemsPanelRoot;
        if (panel is null) return null;
        for (Visual? v = visual; v is not null; v = v.GetVisualParent())
            if (v.GetVisualParent() == panel) return v as Control;
        return null;
    }
}
