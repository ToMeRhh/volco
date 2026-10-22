using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Volco.Core.Controller.Proto;
using ProtoButton = Volco.Core.Controller.Proto.Button;

namespace Volco.App.Views.Hardware;

/// <summary>
/// A small picture of a controller config's controls (hardware-page spec §7.1, §7.2): its sliders
/// as slots with a knob in one row, its buttons under them as round caps, a button with a screen as
/// a small rectangle. Nothing is drawn for a null config.
/// </summary>
public sealed class MiniDrawing : Control
{
    public static readonly StyledProperty<ControllerConfig?> ConfigProperty =
        AvaloniaProperty.Register<MiniDrawing, ControllerConfig?>(nameof(Config));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<MiniDrawing, IBrush?>(nameof(Stroke), Brushes.Gray);

    static MiniDrawing()
    {
        AffectsRender<MiniDrawing>(ConfigProperty, StrokeProperty);
        AffectsMeasure<MiniDrawing>(ConfigProperty);
    }

    /// <summary>The config drawn.</summary>
    public ControllerConfig? Config
    {
        get => GetValue(ConfigProperty);
        set => SetValue(ConfigProperty, value);
    }

    /// <summary>The brush of every mark.</summary>
    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    private const double Step = 12;
    private const double SliderHeight = 24;
    private const double Cap = 8;
    private const double RowGap = 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Config is not { } c) return default;
        var columns = Math.Max(c.Sliders.Count, c.Buttons.Count);
        var width = columns == 0 ? 0 : columns * Step;
        double height = 0;
        if (c.Sliders.Count > 0) height += SliderHeight;
        if (c.Buttons.Count > 0) height += (height > 0 ? RowGap : 0) + Cap;
        return new Size(Math.Min(width, availableSize.Width), height);
    }

    public override void Render(DrawingContext context)
    {
        if (Config is not { } c || Stroke is not { } brush) return;
        var columns = Math.Max(c.Sliders.Count, c.Buttons.Count);
        if (columns == 0) return;
        var step = Math.Min(Step, Bounds.Width / columns);
        var pen = new Pen(brush, 1);

        double y = 0;
        for (int i = 0; i < c.Sliders.Count; i++)
        {
            var x = i * step + step / 2;
            context.DrawRectangle(brush, null, new Rect(x - 1, y, 2, SliderHeight), 1, 1);
            context.DrawRectangle(brush, null, new Rect(x - 3, y + SliderHeight / 2 - 2, 6, 4), 1, 1);
        }
        if (c.Sliders.Count > 0) y += SliderHeight + RowGap;

        for (int i = 0; i < c.Buttons.Count; i++)
        {
            var x = i * step + step / 2;
            if (c.Buttons[i].IndicatorCase == ProtoButton.IndicatorOneofCase.Screen)
                context.DrawRectangle(null, pen, new Rect(x - Cap / 2 + 0.5, y + 0.5, Cap - 1, Cap - 1), 1.5, 1.5);
            else
                context.DrawEllipse(null, pen, new Point(x, y + Cap / 2), Cap / 2 - 0.5, Cap / 2 - 0.5);
        }
    }
}
