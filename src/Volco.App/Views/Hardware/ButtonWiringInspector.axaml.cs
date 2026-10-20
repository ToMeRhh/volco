using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Volco.App.Views.Hardware;

/// <summary>The Hardware inspector for a button; bindings do the work.</summary>
public partial class ButtonWiringInspector : UserControl
{
    public ButtonWiringInspector()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
