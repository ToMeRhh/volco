using Avalonia.Markup.Xaml;

namespace Volco.App.Views.Inspector;

/// <summary>The Controller page's inspector for a slider; bindings do the work.</summary>
public partial class SliderInspector : TileInspector
{
    public SliderInspector()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
