using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Volco.App.ViewModels;

namespace Volco.App.Converters;

/// <summary>[item (friendly name), combo DataContext, container Tag] -> label or secondary text.
/// The labeler is the first <see cref="IDeviceLabeler"/> among the trailing values: the selection
/// box finds it through its ComboBox, dropdown items through the Tag stamped by <see cref="Stamp"/>
/// (a dropdown item cannot reach its ComboBox by an ancestor binding).</summary>
public sealed class DeviceLabelConverter : IMultiValueConverter
{
    public static readonly DeviceLabelConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool secondary = parameter as string == "secondary";
        if (values.Count < 1 || values[0] is not string name) return "";
        var labeler = values.Skip(1).OfType<IDeviceLabeler>().FirstOrDefault();
        if (labeler is null) return secondary ? "" : name;
        var choice = labeler.Describe(name);
        return secondary ? choice.Secondary : choice.Label;
    }

    /// <summary>ContainerPrepared handler for device combo boxes: hands each dropdown item the
    /// combo's labeler (its DataContext) through <see cref="Control.Tag"/>.</summary>
    public static void Stamp(object? sender, ContainerPreparedEventArgs e)
    {
        if (sender is ComboBox { DataContext: IDeviceLabeler labeler } && e.Container is Control container)
            container.Tag = labeler;
    }
}
