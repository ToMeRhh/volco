using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Volco.App.Controls;

/// <summary>
/// One press-action slot: label, action picker, device picker (only for device-taking actions)
/// and a Test button. DataContext is a PressActionViewModel; the device names come from the
/// parent button item via <see cref="DeviceNames"/>.
/// </summary>
public partial class PressActionRow : UserControl
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<PressActionRow, string?>(nameof(Label));

    public static readonly StyledProperty<IEnumerable<string>?> DeviceNamesProperty =
        AvaloniaProperty.Register<PressActionRow, IEnumerable<string>?>(nameof(DeviceNames));

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public IEnumerable<string>? DeviceNames
    {
        get => GetValue(DeviceNamesProperty);
        set => SetValue(DeviceNamesProperty, value);
    }

    public PressActionRow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
