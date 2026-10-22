using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Volco.App.Views.Hardware;

/// <summary>The save bar (hardware-page spec §6.5) over <see cref="Volco.App.ViewModels.Hardware.HardwareViewModel"/>; bindings do the work.</summary>
public partial class SaveBar : UserControl
{
    public SaveBar()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
