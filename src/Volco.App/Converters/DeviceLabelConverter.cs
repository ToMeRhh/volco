using System.Globalization;
using Avalonia.Data.Converters;
using Volco.App.ViewModels;

namespace Volco.App.Converters;

/// <summary>[item (friendly name), combo DataContext (IDeviceLabeler)] -> label or secondary text.</summary>
public sealed class DeviceLabelConverter : IMultiValueConverter
{
    public static readonly DeviceLabelConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2 || values[0] is not string name) return "";
        if (values[1] is not IDeviceLabeler labeler) return parameter as string == "secondary" ? "" : name;
        var choice = labeler.Describe(name);
        return parameter as string == "secondary" ? choice.Secondary : choice.Label;
    }
}
