using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Volco.App.Views.Inspector;

/// <summary>The Controls page's inspector for a button; bindings do the work.</summary>
public partial class ButtonInspector : UserControl
{
    public ButtonInspector()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
