using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Volco.App.ViewModels;

namespace Volco.App.Views.Pages;

public partial class ControllerPage : UserControl
{
    public ControllerPage()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public ControllerPage(ControllerViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
