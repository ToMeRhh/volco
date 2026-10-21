using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Volco.App.Views.Hardware;

/// <summary>The draft's inline confirmation over <see cref="Volco.App.ViewModels.Hardware.HardwareViewModel"/>; bindings do the work.</summary>
public partial class InlineConfirmation : UserControl
{
    public InlineConfirmation()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
