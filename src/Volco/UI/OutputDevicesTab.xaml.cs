using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Volco.App.ViewModels;

namespace Volco.UI;

public sealed partial class OutputDevicesTab : Page
{
    public OutputDevicesViewModel? ViewModel { get; private set; }

    public OutputDevicesTab()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not SettingsViewModel settingsVm)
            return;

        ViewModel = settingsVm.OutputDevices;
        Bindings.Update();
    }
}
