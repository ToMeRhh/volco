using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Volco.Audio;
using Volco.Communication;
using Volco.Config;
using Volco.Input;
using Volco.UI.ViewModels;

namespace Volco.UI;

public sealed partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly SliderProcessor _sliderProcessor;

    public SettingsWindow(ConfigManager configManager, IAudioManager audioManager, IControllerConnection? connection, SliderProcessor sliderProcessor)
    {
        InitializeComponent();

        _viewModel = new SettingsViewModel(configManager, audioManager, connection);
        _sliderProcessor = sliderProcessor;

        // WinUI 3 Window doesn't support Width/Height in XAML; set via AppWindow
        var appWindow = this.AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(720, 600));
        appWindow.Closing += AppWindow_Closing;

        // Subscribe to live slider level updates; marshal to UI thread
        _sliderProcessor.SliderLevelChanged += OnSliderLevel;

        // Select first tab by default
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    private void OnSliderLevel(object? sender, SliderLevelEventArgs e)
        => DispatcherQueue.TryEnqueue(() => _viewModel.Sliders.UpdateLiveLevel(e.SliderIndex, e.Level));

    private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (!App.IsExiting)
        {
            args.Cancel = true;
            sender.Hide();
        }
        else
        {
            _sliderProcessor.SliderLevelChanged -= OnSliderLevel;
        }
    }

    /// <summary>
    /// Called by App after a transport restart to refresh the ConnectionViewModel's live status.
    /// </summary>
    public void UpdateConnection(IControllerConnection? connection)
        => _viewModel.Connection.UpdateConnection(connection);

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item)
        {
            var tag = item.Tag?.ToString();
            switch (tag)
            {
                case "connection":
                    ContentFrame.Navigate(typeof(ConnectionTab), _viewModel);
                    break;
                case "sliders":
                    ContentFrame.Navigate(typeof(SlidersTab), _viewModel);
                    break;
                case "buttons":
                    ContentFrame.Navigate(typeof(ButtonsTab), _viewModel);
                    break;
                case "outputdevices":
                    ContentFrame.Navigate(typeof(OutputDevicesTab), _viewModel);
                    break;
                case "general":
                    ContentFrame.Navigate(typeof(GeneralTab), _viewModel);
                    break;
            }
        }
    }
}
