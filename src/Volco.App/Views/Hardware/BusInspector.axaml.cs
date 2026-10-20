using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Volco.App.Views.Hardware;

/// <summary>The Hardware inspector for an I2C bus; bindings do the work.</summary>
public partial class BusInspector : UserControl
{
    public BusInspector()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
