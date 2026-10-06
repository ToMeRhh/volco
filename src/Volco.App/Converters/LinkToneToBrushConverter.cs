using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Volco.App.Converters;

/// <summary>
/// Maps a <see cref="LinkTone"/> to a theme brush: Good → SuccessBrush, Warning → WarningBrush,
/// Danger → DangerBrush, Neutral → TextTertiaryBrush (a dot) or, with the parameter "Text",
/// TextSecondaryBrush (an explanation line). A dot always sits next to a text label, so state is
/// never shown by colour alone.
/// </summary>
public sealed class LinkToneToBrushConverter : IValueConverter
{
    private static readonly IBrush Fallback = Brushes.Gray;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value as LinkTone? ?? LinkTone.Neutral) switch
        {
            LinkTone.Good => "SuccessBrush",
            LinkTone.Warning => "WarningBrush",
            LinkTone.Danger => "DangerBrush",
            _ => parameter as string == "Text" ? "TextSecondaryBrush" : "TextTertiaryBrush",
        };
        var app = Application.Current;
        return app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var brush) && brush is IBrush b
            ? b
            : Fallback;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
