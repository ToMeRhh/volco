using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Volco.App.ViewModels;

namespace Volco.UI;

public sealed partial class ButtonsTab : Page
{
    public ButtonsViewModel? ViewModel { get; private set; }

    public ButtonsTab()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not SettingsViewModel settingsVm)
            return;

        ViewModel = settingsVm.Buttons;
        Bindings.Update();
    }
}
