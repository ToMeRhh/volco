using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Volco.App.Views.Hardware;

/// <summary>The Hardware inspector for a slider; bindings do the work.</summary>
public partial class SliderWiringInspector : UserControl
{
    public SliderWiringInspector()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
