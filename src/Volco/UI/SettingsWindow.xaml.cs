using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Input;
using Volco.UI.ViewModels;
using Windows.Graphics;

namespace Volco.UI;

public sealed partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly SliderProcessor _sliderProcessor;

    public SettingsWindow(ConfigManager configManager, IAudioEngine audioEngine, IControllerConnection? connection, SliderProcessor sliderProcessor)
    {
        InitializeComponent();

        _viewModel = new SettingsViewModel(configManager, audioEngine, connection);
        _sliderProcessor = sliderProcessor;

        // WinUI 3 Window doesn't support Width/Height in XAML; set via AppWindow
        var appWindow = this.AppWindow;
        appWindow.Resize(new SizeInt32(720, 600));
        if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
        }
        // NOTE: WinUI 3 has no built-in minimum-window-size API; enforcing a hard minimum
        // (~680x560) requires a WM_GETMINMAXINFO window-subclass hook. Deferred — the window
        // is freely resizable with a sensible 720x600 default.
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

    /// <summary>
    /// Called by App after <see cref="IAudioEngine.DevicesChanged"/> fires (Bug 3), so a
    /// replugged/removed device shows up in every open device picker. SettingsViewModel marshals
    /// to the UI thread itself, the same way it captures a dispatcher the way ConnectionViewModel
    /// does — this call itself is safe from any thread.
    /// </summary>
    public void RefreshDevices() => _viewModel.RefreshDevices();

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
