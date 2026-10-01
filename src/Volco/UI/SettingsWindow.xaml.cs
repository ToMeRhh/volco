using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Volco.Core.Runtime;
using Volco.App.ViewModels;
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
        _viewModel = new SettingsViewModel(runtime.Config, runtime.Audio, runtime, new WinUiDispatcher(DispatcherQueue));

        // Fix D: seed every slider row's live level from state the runtime already holds. The
        // firmware only reports a slider's value when it moves, so right after app start (or
        // after this window was hidden and shown again) a slider that hasn't moved would
        // otherwise show a stale/zeroed level forever, waiting on a SliderLevelChanged event that
        // may never come.
        _viewModel.Sliders.SeedLiveLevels(_runtime.GetSliderLevel);

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

        // SettingsViewModel owns the SliderLevelChanged/DevicesChanged subscriptions and marshals
        // them through the WinUiDispatcher above; nothing to subscribe to here.

        // Fix D: AppWindow_Closing below hides rather than closes this window (see its comment),
        // so it is only ever constructed once — re-showing it never re-runs the seeding above.
        // Activated fires every time the window regains activation, including right after that
        // Show()/un-hide, which is exactly when a level that changed while the window was hidden
        // (and therefore missed by SettingsViewModel's SliderLevelChanged subscription) needs to be caught
        // up. Already on the UI thread — no dispatcher marshalling needed.
        Activated += OnActivated;

        // Select first tab by default
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    /// <summary>Fix D: re-seed live levels whenever this window becomes active again (not on
    /// the Deactivated transition Activated also reports).</summary>
    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
            _viewModel.Sliders.SeedLiveLevels(_runtime.GetSliderLevel);
    }

    private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (!WinUiApp.IsExiting)
        {
            args.Cancel = true;
            sender.Hide();
        }
        else
        {
            _viewModel.Dispose();
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
