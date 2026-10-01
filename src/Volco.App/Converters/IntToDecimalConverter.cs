using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Volco.App.Converters;

/// <summary>
/// Adapts an int ViewModel property to NumericUpDown.Value (decimal?). A cleared box (null)
/// is ignored on the way back so the stored port is never overwritten with garbage.
/// </summary>
public sealed class IntToDecimalConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i ? (decimal)i : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is decimal d ? (int)Math.Round(d) : BindingOperations.DoNothing;
}
