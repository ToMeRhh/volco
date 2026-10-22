using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Volco.App.Views.Hardware;

/// <summary>Hardware's title-row buttons over <see cref="Volco.App.ViewModels.Hardware.HardwareViewModel"/>; bindings do the work.</summary>
public partial class HardwareTitleActions : UserControl
{
    public HardwareTitleActions()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
