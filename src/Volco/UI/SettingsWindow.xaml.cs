using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Volco.Core.Audio;
using Volco.Core.Input;
using Volco.Core.Runtime;
using Volco.UI.ViewModels;
using Windows.Graphics;

namespace Volco.UI;

public sealed partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly VolcoRuntime _runtime;

    public SettingsWindow(VolcoRuntime runtime)
    {
        InitializeComponent();

        _runtime = runtime;
        _viewModel = new SettingsViewModel(runtime.Config, runtime.Audio, runtime);

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

        // Subscribe to live slider level updates and device-list changes. Both can be raised on
        // any thread (the audio thread, SliderProcessor's coalescing timer, or a transport
        // thread), so marshal to the UI thread via TryEnqueue rather than handling synchronously.
        _runtime.SliderLevelChanged += OnSliderLevel;
        _runtime.DevicesChanged += OnDevicesChanged;

        // Select first tab by default
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    private void OnSliderLevel(object? sender, SliderLevelEventArgs e)
        => DispatcherQueue.TryEnqueue(() => _viewModel.Sliders.UpdateLiveLevel(e.SliderIndex, e.Level));

    /// <summary>
    /// Raised by <see cref="VolcoRuntime.DevicesChanged"/> for every kind of device change
    /// (added/removed/state/default — Bug 3), so a replugged/removed device shows up in every
    /// open device picker.
    /// </summary>
    private void OnDevicesChanged(object? sender, DevicesChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(() => _viewModel.RefreshDevices());

    private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (!App.IsExiting)
        {
            args.Cancel = true;
            sender.Hide();
        }
        else
        {
            _runtime.SliderLevelChanged -= OnSliderLevel;
            _runtime.DevicesChanged -= OnDevicesChanged;
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
