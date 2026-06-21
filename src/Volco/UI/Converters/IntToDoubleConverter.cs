using System;
using Microsoft.UI.Xaml.Data;

namespace Volco.UI.Converters;

/// <summary>
/// Converts between <see cref="int"/> and <see cref="double"/> for binding
/// integer VM properties (e.g., port numbers) to <see cref="Microsoft.UI.Xaml.Controls.NumberBox.Value"/>
/// which is typed as <c>double</c>.
/// </summary>
public sealed class IntToDoubleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is int i ? (double)i : 0.0;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is double d ? (int)d : 0;
}
