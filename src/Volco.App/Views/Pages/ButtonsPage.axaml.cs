using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Volco.App.ViewModels;

namespace Volco.App.Views.Pages;

/// <summary>Per-button press-action mappings and timing. Bindings do all the work.</summary>
public partial class ButtonsPage : UserControl
{
    public ButtonsPage()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public ButtonsPage(ButtonsViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
