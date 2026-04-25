using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Volco.Config;

namespace Volco.UI;

public sealed partial class GeneralTab : Page
{
    private ConfigManager? _configManager;
    private bool _isLoading = true;

    public GeneralTab()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is ConfigManager configManager)
            _configManager = configManager;

        if (_configManager == null) return;

        _isLoading = true;
        DebugLoggingCheckBox.IsChecked = _configManager.Config.DebugLogging;
        _isLoading = false;
    }

    private void DebugLogging_Changed(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_isLoading || _configManager == null) return;
        var enabled = DebugLoggingCheckBox.IsChecked == true;
        _configManager.Config.DebugLogging = enabled;
        AppLog.IsDebugEnabled = enabled;
        _configManager.Save();
    }
}
