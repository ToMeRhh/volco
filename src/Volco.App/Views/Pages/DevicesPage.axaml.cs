using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Volco.App.ViewModels;

namespace Volco.App.Views.Pages;

/// <summary>Device nicknames and icons (spec §6.1). Bindings do all the work.</summary>
public partial class DevicesPage : UserControl
{
    public DevicesPage() => AvaloniaXamlLoader.Load(this);

    public DevicesPage(DevicesViewModel viewModel) : this() => DataContext = viewModel;
}
