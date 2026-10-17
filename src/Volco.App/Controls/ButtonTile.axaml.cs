using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Volco.App.Controls;

/// <summary>One button on the Controls page's drawing. The page selects it on a click, Enter or
/// Space, and moves the focus between tiles with the arrow keys.</summary>
public partial class ButtonTile : UserControl
{
    public ButtonTile()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
