using System.Globalization;
using Avalonia.Data.Converters;
using Volco.App.Controls;
using Volco.Core.Communication;

namespace Volco.App.Converters;

/// <summary>ScreenIcon → its vector art (for icon tiles and the icon picker).</summary>
public sealed class ScreenIconToGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ScreenIcon icon ? ScreenIcons.Geometry(icon) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
