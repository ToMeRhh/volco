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
        // Start at sign-in can also change in the OS's own startup settings: re-read on each show.
        AttachedToVisualTree += (_, _) => (DataContext as GeneralViewModel)?.RefreshStartAtSignIn();
    }

    public GeneralPage(GeneralViewModel viewModel, DevicesViewModel? devices = null) : this()
    {
        DataContext = viewModel;
        if (devices is not null)
        {
            Devices = new DevicesSection(devices);
            this.FindControl<ContentControl>("DevicesHost")!.Content = Devices;
        }
        if (viewModel.ShowUpdates)
        {
            Updates = new UpdatesPanel();
            this.FindControl<ContentControl>("UpdatesHost")!.Content = Updates;
        }
    }

    /// <summary>The Updates panel, when this is a release build.</summary>
    internal UpdatesPanel? Updates { get; }

    /// <summary>The Devices section, when the page was given its view model.</summary>
    internal DevicesSection? Devices { get; }
}
