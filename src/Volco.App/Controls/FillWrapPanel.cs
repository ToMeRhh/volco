using Avalonia;
using Avalonia.Controls;

namespace Volco.App.Controls;

/// <summary>
/// Wraps children into rows of equal-width cells that fill the row, like CSS
/// <c>repeat(auto-fit, minmax(MinItemWidth, 1fr))</c>. A row is as tall as its tallest child.
/// Hidden children are skipped.
/// </summary>
public sealed class FillWrapPanel : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<FillWrapPanel, double>(nameof(MinItemWidth), 84);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<FillWrapPanel, double>(nameof(Spacing), 6);

    static FillWrapPanel()
    {
        AffectsMeasure<FillWrapPanel>(MinItemWidthProperty, SpacingProperty);
    }

    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private int VisibleCount()
    {
        var n = 0;
        foreach (var child in Children)
            if (child.IsVisible) n++;
        return n;
    }

    private int Columns(double width, int count)
    {
        if (count == 0) return 1;
        var cols = (int)Math.Floor((width + Spacing) / (MinItemWidth + Spacing));
        return Math.Max(1, Math.Min(count, cols));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = VisibleCount();
        if (count == 0) return default;

        if (double.IsInfinity(availableSize.Width))
        {
            double width = 0, height = 0;
            var first = true;
            foreach (var child in Children)
            {
                if (!child.IsVisible) continue;
                child.Measure(new Size(MinItemWidth, availableSize.Height));
                var w = Math.Max(MinItemWidth, child.DesiredSize.Width);
                if (w > MinItemWidth)
                    child.Measure(new Size(w, availableSize.Height));
                width += (first ? 0 : Spacing) + w;
                height = Math.Max(height, child.DesiredSize.Height);
                first = false;
            }
            return new Size(width, height);
        }

        var cols = Columns(availableSize.Width, count);
        var cell = Math.Max(0, (availableSize.Width - (cols - 1) * Spacing) / cols);
        double total = 0, rowHeight = 0;
        var index = 0;
        var rows = 0;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            child.Measure(new Size(cell, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            index++;
            if (index % cols == 0 || index == count)
            {
                total += rowHeight;
                rows++;
                rowHeight = 0;
            }
        }
        total += Math.Max(0, rows - 1) * Spacing;
        return new Size(availableSize.Width, total);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = VisibleCount();
        if (count == 0) return finalSize;

        var cols = Columns(finalSize.Width, count);
        var cell = Math.Max(0, (finalSize.Width - (cols - 1) * Spacing) / cols);
        var visible = Children.Where(c => c.IsVisible).ToList();
        double y = 0;
        for (var start = 0; start < visible.Count; start += cols)
        {
            var end = Math.Min(start + cols, visible.Count);
            double rowHeight = 0;
            for (var i = start; i < end; i++)
                rowHeight = Math.Max(rowHeight, visible[i].DesiredSize.Height);
            for (var i = start; i < end; i++)
                visible[i].Arrange(new Rect((i - start) * (cell + Spacing), y, cell, rowHeight));
            y += rowHeight + Spacing;
        }
        return finalSize;
    }
}
