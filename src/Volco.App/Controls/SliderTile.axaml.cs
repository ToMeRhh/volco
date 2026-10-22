using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Volco.App.Controls;

/// <summary>One slider on the Controller page's drawing. The page selects it on a click, Enter or
/// Space, and moves the focus between tiles with the arrow keys.</summary>
public partial class SliderTile : UserControl
{
    public SliderTile()
    {
        AvaloniaXamlLoader.Load(this);
        TileFlash.Attach(this, this.FindControl<Panel>("TileRoot")!);
    }
}
