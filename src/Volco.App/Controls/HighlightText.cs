using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Volco.App.Controls;

/// <summary>A search result's name with its matched characters bold in the accent colour
/// (spec controls-page §6.3).</summary>
public class HighlightText : TextBlock
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<HighlightText, string?>(nameof(Source));

    public static readonly StyledProperty<IReadOnlyList<int>?> HighlightProperty =
        AvaloniaProperty.Register<HighlightText, IReadOnlyList<int>?>(nameof(Highlight));

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Positions in <see cref="Source"/> to highlight.</summary>
    public IReadOnlyList<int>? Highlight
    {
        get => GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    /// <summary>The highlighted runs' text, for tests.</summary>
    internal IEnumerable<string> HighlightedRuns
        => Inlines?.OfType<Run>().Where(r => r.FontWeight == FontWeight.Bold).Select(r => r.Text ?? "") ?? [];

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == HighlightProperty
            || change.Property == ActualThemeVariantProperty) Rebuild();
    }

    private void Rebuild()
    {
        var text = Source ?? "";
        var marks = new HashSet<int>(Highlight ?? Array.Empty<int>());
        var inlines = new InlineCollection();
        var start = 0;
        while (start < text.Length)
        {
            var marked = marks.Contains(start);
            var end = start + 1;
            while (end < text.Length && marks.Contains(end) == marked) end++;
            var run = new Run(text[start..end]);
            if (marked)
            {
                run.FontWeight = FontWeight.Bold;
                if (this.TryFindResource("AccentBrush", ActualThemeVariant, out var accent) && accent is IBrush brush)
                    run.Foreground = brush;
            }
            inlines.Add(run);
            start = end;
        }
        Inlines = inlines;
    }
}
