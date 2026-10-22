using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Volco.App.Views.Hardware;

/// <summary>The status banner over <see cref="Volco.App.ViewModels.Hardware.ControllerStatusViewModel"/>;
/// bindings do the work.</summary>
public partial class StatusBanner : UserControl
{
    public StatusBanner()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
