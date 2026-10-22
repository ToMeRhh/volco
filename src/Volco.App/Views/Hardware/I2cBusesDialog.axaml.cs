using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.ViewModels.Hardware;

namespace Volco.App.Views.Hardware;

/// <summary>The I2C buses dialog over <see cref="I2cBusesViewModel"/>: bindings do the work, except
/// that the card of the bus it was opened for scrolls into view.</summary>
public partial class I2cBusesDialog : UserControl
{
    public I2cBusesDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() =>
        {
            var card = this.GetVisualDescendants().OfType<Border>()
                           .FirstOrDefault(b => b.Name == "BusCard" && b.DataContext is BusWiringViewModel { IsFocused: true });
            card?.BringIntoView();
        }, DispatcherPriority.Loaded);
    }
}
