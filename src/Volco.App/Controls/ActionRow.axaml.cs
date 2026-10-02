using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Volco.App.Controls;

/// <summary>
/// One action row of a button card: label, grouped action picker, Test, and the chosen action's
/// settings (a device, an A/B output pair or a preset) indented underneath. DataContext is an
/// <see cref="ViewModels.ActionSlotViewModel"/>.
/// </summary>
public partial class ActionRow : UserControl
{
    public ActionRow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void DeviceCombo_ContainerPrepared(object? sender, ContainerPreparedEventArgs e)
        => Converters.DeviceLabelConverter.Stamp(sender, e);
}
