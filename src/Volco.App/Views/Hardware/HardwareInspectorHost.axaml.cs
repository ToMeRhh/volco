using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Volco.App.Views.Hardware;

/// <summary>The controller's part of the side panel over <see cref="Volco.App.ViewModels.Hardware.HardwareViewModel"/>:
/// the status menu's preset chooser and Controller settings; bindings do the work.</summary>
public partial class HardwareInspectorHost : UserControl
{
    public HardwareInspectorHost()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
