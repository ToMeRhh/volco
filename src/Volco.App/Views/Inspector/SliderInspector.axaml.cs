using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Volco.App.Views.Inspector;

/// <summary>The Controls page's inspector for a slider; bindings do the work.</summary>
public partial class SliderInspector : UserControl
{
    public SliderInspector()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
