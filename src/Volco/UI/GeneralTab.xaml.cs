using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Volco.App.ViewModels;

namespace Volco.UI;

public sealed partial class GeneralTab : Page
{
    public GeneralViewModel? ViewModel { get; private set; }

    public GeneralTab()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not SettingsViewModel settingsVm)
            return;

        ViewModel = settingsVm.General;
        Bindings.Update();
    }
}
