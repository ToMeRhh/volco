using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Volco.App.ViewModels;

namespace Volco.App.Views.Pages;

/// <summary>Device nicknames and icons (spec §6.1), a section of the General page. Bindings do
/// all the work.</summary>
public partial class DevicesSection : UserControl
{
    public DevicesSection()
    {
        AvaloniaXamlLoader.Load(this);
        // The view model is cached and other pages change device references, so rebuild on each show.
        AttachedToVisualTree += (_, _) => (DataContext as DevicesViewModel)?.Refresh();
    }

    public DevicesSection(DevicesViewModel viewModel) : this() => DataContext = viewModel;
}
