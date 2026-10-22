using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Volco.App.Views.Hardware;

/// <summary>The sidebar's status line over <see cref="Volco.App.ViewModels.Hardware.ControllerStatusViewModel"/>;
/// bindings do the work, and the window pins the compact sidebar open while its menu is.</summary>
public partial class SidebarStatus : UserControl
{
    public SidebarStatus()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>The line itself: a click opens the menu.</summary>
    public Button LineButton => this.FindControl<Button>("StatusLineButton")!;
}
