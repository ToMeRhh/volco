using System.Globalization;
using Avalonia.Data.Converters;
using Volco.App.Controls;
using Volco.Core.Communication;

namespace Volco.App.Converters;

/// <summary>ScreenIcon → its 28×28 bitmap (for icon tiles and the icon picker).</summary>
public sealed class ScreenIconToBitmapConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ScreenIcon icon ? ScreenIcons.Bitmap(icon) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
