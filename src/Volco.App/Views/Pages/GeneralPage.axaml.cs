using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Volco.App.Controls;
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
        if (viewModel.ShowUpdates)
        {
            Updates = new UpdatesPanel(viewModel.ConfigManager);
            this.FindControl<ContentControl>("UpdatesHost")!.Content = Updates;
        }
    }

    /// <summary>The Updates panel, when this is a release build.</summary>
    internal UpdatesPanel? Updates { get; }
}
