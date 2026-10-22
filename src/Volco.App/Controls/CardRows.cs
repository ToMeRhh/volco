using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Volco.App.Controls;

/// <summary>
/// The rows of an inspector card (controller-page spec §4.3): a vertical stack with a hairline
/// between each two visible rows, so a row that hides takes its line with it. The lines sit in the
/// 1 px spacing, under nothing a row draws.
/// </summary>
public class CardRows : StackPanel
{
    public static readonly StyledProperty<IBrush?> DividerBrushProperty =
        AvaloniaProperty.Register<CardRows, IBrush?>(nameof(DividerBrush));

    static CardRows()
    {
        AffectsRender<CardRows>(DividerBrushProperty);
        SpacingProperty.OverrideDefaultValue<CardRows>(1);
    }

    /// <summary>The hairlines' brush.</summary>
    public IBrush? DividerBrush
    {
        get => GetValue(DividerBrushProperty);
        set => SetValue(DividerBrushProperty, value);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        InvalidateVisual();   // the lines follow the rows
        return base.ArrangeOverride(finalSize);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (DividerBrush is not { } brush) return;
        var first = true;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            if (!first) context.FillRectangle(brush, new Rect(0, child.Bounds.Top - Spacing, Bounds.Width, Spacing));
            first = false;
        }
    }
}
