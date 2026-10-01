using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Volco.App.ViewModels;

namespace Volco.App.Views.Pages;

/// <summary>The two-device toggle pair pickers. Bindings do all the work.</summary>
public partial class OutputDevicesPage : UserControl
{
    public OutputDevicesPage()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public OutputDevicesPage(OutputDevicesViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
