using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Volco.UI.Converters;

/// <summary>
/// Converts a <see cref="bool"/> to a <see cref="Visibility"/>.
///   true  → Visible
///   false → Collapsed
/// Pass parameter="Invert" to reverse the mapping.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool boolValue = value is bool b && b;
        bool invert = parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);
        return (boolValue ^ invert) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
