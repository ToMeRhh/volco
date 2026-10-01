using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Volco.Core.Communication;

namespace Volco.App.Converters;

/// <summary>
/// Maps a <see cref="ConnectionStatus"/> to the status-dot brush from the theme: Connected →
/// SuccessBrush, Error → DangerBrush, anything else → TextTertiaryBrush. The dot always sits next
/// to a text label, so state is never shown by colour alone.
/// </summary>
public sealed class ConnectionStatusToBrushConverter : IValueConverter
{
    private static readonly IBrush Fallback = Brushes.Gray;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is ConnectionStatus status
            ? status switch
            {
                ConnectionStatus.Connected => "SuccessBrush",
                ConnectionStatus.Error => "DangerBrush",
                _ => "TextTertiaryBrush",
            }
            : "TextTertiaryBrush";
        var app = Application.Current;
        return app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var brush) && brush is IBrush b
            ? b
            : Fallback;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
