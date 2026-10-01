using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Volco.App.ViewModels;

namespace Volco.App.Views.Pages;

public partial class ConnectionPage : UserControl
{
    public ConnectionPage()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public ConnectionPage(ConnectionViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
