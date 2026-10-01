using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Volco.App.ViewModels;

namespace Volco.App.Views.Pages;

public partial class GeneralPage : UserControl
{
    public GeneralPage()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public GeneralPage(GeneralViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
