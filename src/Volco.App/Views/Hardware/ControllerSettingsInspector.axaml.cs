using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Volco.App.Views.Hardware;

/// <summary>The Hardware inspector for the controller's settings; bindings do the work.</summary>
public partial class ControllerSettingsInspector : UserControl
{
    public ControllerSettingsInspector()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
