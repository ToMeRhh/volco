using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Volco.Audio;
using Volco.Config;

namespace Volco.UI;

public sealed partial class SettingsWindow : Window
{
    private readonly ConfigManager _configManager;
    private readonly IAudioManager _audioManager;

    public SettingsWindow(ConfigManager configManager, IAudioManager audioManager)
    {
        InitializeComponent();
        _configManager = configManager;
        _audioManager = audioManager;

        // WinUI 3 Window doesn't support Width/Height in XAML; set via AppWindow
        var appWindow = this.AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(600, 500));
        appWindow.Closing += AppWindow_Closing;

        // Select first tab by default
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (!App.IsExiting)
        {
            args.Cancel = true;
            sender.Hide();
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item)
        {
            var tag = item.Tag?.ToString();
            switch (tag)
            {
                case "connection":
                    ContentFrame.Navigate(typeof(ConnectionTab), (_configManager, ((App)Application.Current).Connection));
                    break;
                case "sliders":
                    ContentFrame.Navigate(typeof(SlidersTab), (_configManager, _audioManager));
                    break;
                case "buttons":
                    ContentFrame.Navigate(typeof(ButtonsTab), (_configManager, _audioManager));
                    break;
                case "outputdevices":
                    ContentFrame.Navigate(typeof(OutputDevicesTab), (_configManager, _audioManager));
                    break;
            }
        }
    }
}
