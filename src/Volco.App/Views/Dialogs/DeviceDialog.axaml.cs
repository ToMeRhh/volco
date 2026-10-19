using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Volco.App.Views.Dialogs;

/// <summary>The device dialog's view. Bindings do the work; the page places the focus on open
/// (the nickname box, or the icon grid) and handles Escape and the scrim.</summary>
public partial class DeviceDialog : UserControl
{
    public DeviceDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
