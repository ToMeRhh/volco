using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Volco.App.ViewModels;

namespace Volco.UI;

public sealed partial class ConnectionTab : Page
{
    public ConnectionViewModel? ViewModel { get; private set; }

    public ConnectionTab()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        // Task 14 will pass SettingsViewModel as the navigation parameter.
        // Guard against old tuple-based callers (pre-Task-14 SettingsWindow).
        if (e.Parameter is not SettingsViewModel settingsVm)
            return;

        ViewModel = settingsVm.Connection;
        Bindings.Update();
    }
}
