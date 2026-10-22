using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Metadata;

namespace Volco.App.Controls;

/// <summary>
/// The rows of an inspector card (controller-page spec §4.3): a vertical stack with a hairline
/// between each two visible rows, so a row that hides takes its line with it. The lines sit in a
/// 1 px gap, under nothing a row draws. A control rather than a StackPanel: a Panel's Render is
/// sealed.
/// </summary>
public class CardRows : Control
{
    private const double Gap = 1;

    public static readonly StyledProperty<IBrush?> DividerBrushProperty =
        AvaloniaProperty.Register<CardRows, IBrush?>(nameof(DividerBrush));

    static CardRows()
    {
        AffectsRender<CardRows>(DividerBrushProperty);
    }

    public CardRows()
    {
        Children.CollectionChanged += OnChildrenChanged;
    }

    /// <summary>The rows, top to bottom.</summary>
    [Content]
    public Avalonia.Controls.Controls Children { get; } = new();

    /// <summary>The hairlines' brush.</summary>
    public IBrush? DividerBrush
    {
        get => GetValue(DividerBrushProperty);
        set => SetValue(DividerBrushProperty, value);
    }

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            LogicalChildren.Clear();
            VisualChildren.Clear();
            LogicalChildren.AddRange(Children);
            VisualChildren.AddRange(Children);
        }
        else
        {
            if (e.OldItems is not null)
                foreach (Control child in e.OldItems)
                {
                    LogicalChildren.Remove(child);
                    VisualChildren.Remove(child);
                }
            if (e.NewItems is not null)
            {
                var index = e.NewStartingIndex;
                foreach (Control child in e.NewItems)
                {
                    LogicalChildren.Insert(index, child);
                    VisualChildren.Insert(index, child);
                    index++;
                }
            }
        }
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var limit = availableSize.WithHeight(double.PositiveInfinity);
        double width = 0, height = 0;
        var first = true;
        foreach (var child in Children)
        {
            child.Measure(limit);
            if (!child.IsVisible) continue;
            if (!first) height += Gap;
            first = false;
            width = Math.Max(width, child.DesiredSize.Width);
            height += child.DesiredSize.Height;
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        var first = true;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            if (!first) y += Gap;
            first = false;
            child.Arrange(new Rect(0, y, finalSize.Width, child.DesiredSize.Height));
            y += child.DesiredSize.Height;
        }
        InvalidateVisual();   // the lines follow the rows
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        if (DividerBrush is not { } brush) return;
        var first = true;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            if (!first) context.FillRectangle(brush, new Rect(0, child.Bounds.Top - Gap, Bounds.Width, Gap));
            first = false;
        }
    }
}
