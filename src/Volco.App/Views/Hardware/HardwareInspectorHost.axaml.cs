using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Volco.App.Views.Hardware;

/// <summary>The Hardware side panel's inspector over <see cref="Volco.App.ViewModels.Hardware.HardwareViewModel"/>; bindings do the work.</summary>
public partial class HardwareInspectorHost : UserControl
{
    public HardwareInspectorHost()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
