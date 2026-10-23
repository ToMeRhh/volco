using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.ViewModels;
using Volco.App.ViewModels.Hardware;
using Volco.App.Views.Hardware;
using Volco.App.Views.Pages;
using Volco.App.Views.Stage;
using Volco.Core.Runtime;

namespace Volco.App.Views;

public sealed class NavItem(string label, string tag, Geometry? icon, Func<Control> createPage) : INotifyPropertyChanged
{
    private bool _showDot;

    public string Label { get; } = label;
    public string Tag { get; } = tag;
    public Geometry? Icon { get; } = icon;
    public Func<Control> CreatePage { get; } = createPage;

    /// <summary>A dot after the label (the Controller entry: a firmware update is available).</summary>
    public bool ShowDot
    {
        get => _showDot;
        set
        {
            if (_showDot == value) return;
            _showDot = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowDot)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class SettingsWindow : Window
{
    /// <summary>How long an "… · Undo" message stays up.</summary>
    internal static readonly TimeSpan ToastTimeout = TimeSpan.FromSeconds(6);

    private readonly SettingsViewModel _vm;
    private readonly ListBox _navList;
    private readonly TransitioningContentControl _pageHost;
    private StagePage? _stage;
    private readonly Dictionary<string, Control> _pages = new();
    private IDisposable? _toastTimer;
    private readonly NavItem _controllerNav;
    private readonly HardwareViewModel _hardware;
    private readonly Border _compareOverlay;
    private readonly ContentControl _compareHost;
    private readonly Border _connectionOverlay;
    private readonly ContentControl _connectionHost;
    private readonly Border _calibrationOverlay;
    private readonly ContentControl _calibrationHost;
    private readonly Border _busesOverlay;
    private readonly ContentControl _busesHost;
    private readonly Border _pinOverlay;
    private readonly ContentControl _pinHost;
    private readonly Border _reinstallOverlay;
    private readonly Border _leaveOverlay;
    private readonly ContentControl _leaveHost;
    private readonly Border _attentionOverlay;
    private readonly Border _attentionCard;
    private readonly Border _progressOverlay;
    private readonly Border _progressCard;

    /// <summary>Set by the composition root at shutdown so Closing is not cancelled.</summary>
    internal bool AllowClose { get; set; }

    public SettingsWindow(SettingsViewModel vm, VolcoRuntime runtime)
    {
        _vm = vm;
        AvaloniaXamlLoader.Load(this);
        DataContext = vm;
        _navList = this.FindControl<ListBox>("NavList")!;
        _pageHost = this.FindControl<TransitioningContentControl>("PageHost")!;
        _pageHost.PageTransition = null;

        _navList.ItemsSource = new[]
        {
            Nav("General", "general", "Icon.Settings", () => new GeneralPage(vm.General)),
            _controllerNav = Nav("Controller", "controller", "Icon.Sliders", Stage),
        };
        _navList.SelectionChanged += OnNavSelectionChanged;
        _navList.SelectedIndex = 0;

        vm.Toast.PropertyChanged += OnToastChanged;
        _controllerNav.ShowDot = vm.ControllerNavDot;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.ControllerNavDot)) _controllerNav.ShowDot = vm.ControllerNavDot;
        };
        Closing += OnClosing;

        // The notices load on first open (~45 KB); Escape or a click on the scrim closes them.
        vm.About.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(AboutViewModel.IsNoticesOpen) || !vm.About.IsNoticesOpen) return;
            var box = this.FindControl<TextBox>("NoticesBox")!;
            if (string.IsNullOrEmpty(box.Text)) box.Text = LoadNotices();
        };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        this.FindControl<Border>("NoticesOverlay")!.PointerPressed += OnNoticesOverlayPressed;

        // The controller's status is on every page (controller-page spec §5): the actions that
        // belong to the drawing go to the Controller page first (arriving never asks to leave), and
        // its dialogs open over the whole window.
        _hardware = vm.Hardware;
        _hardware.Status.RequestController += (_, _) =>
        {
            ShowPage("controller");
            // The drawing is what the action changes, so a Controller page dialog over it (and the
            // device dialog over that) closes.
            _vm.ControlsIfCreated?.CloseDialog();
        };
        _hardware.PropertyChanged += OnHardwareChanged;
        _compareOverlay = this.FindControl<Border>("CompareOverlay")!;
        _compareHost = this.FindControl<ContentControl>("CompareHost")!;
        _connectionOverlay = this.FindControl<Border>("ConnectionOverlay")!;
        _connectionHost = this.FindControl<ContentControl>("ConnectionHost")!;
        _calibrationOverlay = this.FindControl<Border>("CalibrationOverlay")!;
        _calibrationHost = this.FindControl<ContentControl>("CalibrationHost")!;
        _busesOverlay = this.FindControl<Border>("BusesOverlay")!;
        _busesHost = this.FindControl<ContentControl>("BusesHost")!;
        _pinOverlay = this.FindControl<Border>("PinOverlay")!;
        _pinHost = this.FindControl<ContentControl>("PinHost")!;
        _reinstallOverlay = this.FindControl<Border>("ReinstallOverlay")!;
        _leaveOverlay = this.FindControl<Border>("LeaveOverlay")!;
        _leaveHost = this.FindControl<ContentControl>("LeaveHost")!;
        _attentionOverlay = this.FindControl<Border>("AttentionOverlay")!;
        _attentionCard = this.FindControl<Border>("AttentionCard")!;
        _progressOverlay = this.FindControl<Border>("ProgressOverlay")!;
        _progressCard = this.FindControl<Border>("ProgressCard")!;
        foreach (var overlay in new[] { _compareOverlay, _connectionOverlay, _calibrationOverlay, _busesOverlay, _pinOverlay, _reinstallOverlay, _leaveOverlay, _attentionOverlay, _progressOverlay })
            overlay.PointerPressed += OnScrimPressed;
        // Compare is opened from the banner, which goes once the setups agree.
        TrackFocus(_compareOverlay, this.FindControl<Border>("CompareCard")!, StatusLineButton);
        // Connection and Reinstall are opened from the status line's menu, whose item is gone once
        // it closes: the focus goes back to the status line.
        TrackFocus(_connectionOverlay, this.FindControl<Border>("ConnectionCard")!, StatusLineButton);
        TrackFocus(_reinstallOverlay, this.FindControl<Border>("ReinstallConfirm")!, StatusLineButton);
        TrackFocus(_leaveOverlay, this.FindControl<Border>("LeaveCard")!);
        // Re-calibrate is in the inspector, which stays: the focus goes back to it, else to the
        // Controller page.
        TrackFocus(_calibrationOverlay, this.FindControl<Border>("CalibrationCard")!, ControllerPageFocus);
        // So is the buses dialog's opener (Controller settings' Edit, a button's bus link).
        TrackFocus(_busesOverlay, this.FindControl<Border>("BusesCard")!, ControllerPageFocus);
        // The pin dialog's field stays (in the inspector or the I2C buses dialog): the focus goes
        // back to it.
        TrackFocus(_pinOverlay, this.FindControl<Border>("PinCard")!, ControllerPageFocus);
        // The attention dialog opens by itself (a state began), so the focus goes back to where it
        // was, else to the status line.
        TrackFocus(_attentionOverlay, _attentionCard, StatusLineButton);
        // It shows only while the window does: a state that began while it was hidden shows (and
        // takes the focus) when it opens.
        _hardware.Status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ControllerStatusViewModel.IsAttentionOpen)) UpdateAttention();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty) UpdateAttention();
        };
        UpdateAttention();
        // The progress dialog's opener may be gone (the save bar's Save): the focus goes back to
        // the Controller page when it shows, else to the status line.
        TrackFocus(_progressOverlay, _progressCard, ControllerPageFocus);
        // At the end the focus moves to the next step (else Close), which can take it now.
        _hardware.Status.Progress.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProgressDialogViewModel.IsRunning) && _hardware.Status.Progress.IsDone)
                Dispatcher.UIThread.Post(FocusProgressResult);
        };
        UpdateCompare();
        UpdateConnection();
        UpdateCalibration();
        UpdateBuses();
        UpdatePinDialog();
        UpdateLeavePrompt();

        // The status line's menu is a popup of its own: the mouse on it is off the sidebar, which
        // would close the compact sidebar (and the line under the menu). While it is open the
        // sidebar stays open ("pinned").
        var sidebar = this.FindControl<Grid>("SidebarHost")!;
        if (StatusLineButton()?.Flyout is { } menu)
        {
            menu.Opened += (_, _) => sidebar.Classes.Add("pinned");
            menu.Closed += (_, _) => sidebar.Classes.Remove("pinned");
        }
    }

    /// <summary>Whether the focus can go back to <paramref name="element"/>: shown in this window
    /// (a closed menu's item is not).</summary>
    private bool CanFocus(IInputElement? element)
        => element is InputElement { IsEffectivelyVisible: true, IsEffectivelyEnabled: true, Focusable: true } e
           && e.IsAttachedToVisualTree() && ReferenceEquals(TopLevel.GetTopLevel(e), this);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // The progress dialog is above everything: while the operation runs Escape does nothing,
        // afterwards it closes the dialog; no key reaches the window under it.
        if (_progressOverlay.IsVisible)
        {
            if (e.Key == Key.Escape)
            {
                _hardware.Status.Progress.Close();
                e.Handled = true;
            }
            else if (e.Source is not Visual inside || !_progressCard.IsVisualAncestorOf(inside)) e.Handled = true;
            return;
        }
        if (_vm.About.IsNoticesOpen)
        {
            if (e.Key != Key.Escape) return;
            _vm.About.CloseNoticesCommand.Execute(null);
            e.Handled = true;
            return;
        }
        // The status's dialogs and the leave prompt are modal: Escape closes the open one (the leave
        // prompt's is Stay), and no key reaches the window under it.
        var open = OpenCardOverlay();
        if (open is null) return;
        if (e.Key == Key.Escape)
        {
            // Escape first closes an open dropdown in the dialog (the Connection dialog's port);
            // only the next one closes the dialog.
            var dropDown = open.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(c => c.IsDropDownOpen);
            if (dropDown is not null) dropDown.IsDropDownOpen = false;
            else CloseCardOverlay(open);
            e.Handled = true;
        }
        else if (e.Source is not Visual source || !open.IsVisualAncestorOf(source)) e.Handled = true;
    }

    /// <summary>The status's dialog that is open, if any.</summary>
    private Border? OpenCardOverlay()
        => _attentionOverlay.IsVisible ? _attentionOverlay
         : _leaveOverlay.IsVisible ? _leaveOverlay
         : _reinstallOverlay.IsVisible ? _reinstallOverlay
         : _calibrationOverlay.IsVisible ? _calibrationOverlay
         : _pinOverlay.IsVisible ? _pinOverlay
         : _busesOverlay.IsVisible ? _busesOverlay
         : _connectionOverlay.IsVisible ? _connectionOverlay
         : _compareOverlay.IsVisible ? _compareOverlay
         : null;

    private void CloseCardOverlay(Border overlay)
    {
        if (ReferenceEquals(overlay, _progressOverlay)) _hardware.Status.Progress.Close();   // only once it ended
        else if (ReferenceEquals(overlay, _attentionOverlay)) _hardware.Status.NotNowCommand.Execute(null);
        else if (ReferenceEquals(overlay, _leaveOverlay)) _hardware.CloseLeavePrompt();
        else if (ReferenceEquals(overlay, _reinstallOverlay)) _hardware.Status.Controller.CancelInstallCommand.Execute(null);
        else if (ReferenceEquals(overlay, _connectionOverlay)) _hardware.CloseConnection();
        else if (ReferenceEquals(overlay, _calibrationOverlay)) _hardware.Calibration?.CancelCommand.Execute(null);
        else if (ReferenceEquals(overlay, _pinOverlay)) _hardware.ClosePinDialog();
        else if (ReferenceEquals(overlay, _busesOverlay)) _hardware.CloseBuses();
        else _hardware.CloseCompare();
    }

    /// <summary>A click on a status dialog's scrim closes it.</summary>
    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender) || sender is not Border overlay) return;   // a click inside the card
        CloseCardOverlay(overlay);
        e.Handled = true;
    }

    private void OnHardwareChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HardwareViewModel.IsCompareOpen) or nameof(HardwareViewModel.Compare)) UpdateCompare();
        else if (e.PropertyName is nameof(HardwareViewModel.IsConnectionDialogOpen) or nameof(HardwareViewModel.ConnectionDialog)) UpdateConnection();
        else if (e.PropertyName is nameof(HardwareViewModel.LeavePrompt)) UpdateLeavePrompt();
        else if (e.PropertyName is nameof(HardwareViewModel.Calibration)) UpdateCalibration();
        else if (e.PropertyName is nameof(HardwareViewModel.Buses)) UpdateBuses();
        else if (e.PropertyName is nameof(HardwareViewModel.PinDialog)) UpdatePinDialog();
    }

    /// <summary>The attention dialog over the window while the status asks for it and the window shows.</summary>
    private void UpdateAttention() => _attentionOverlay.IsVisible = IsVisible && _hardware.Status.IsAttentionOpen;

    /// <summary>The leave prompt's card over the window while one is open.</summary>
    private void UpdateLeavePrompt()
    {
        var prompt = _hardware.LeavePrompt;
        _leaveHost.Content = prompt;
        _leaveOverlay.IsVisible = prompt is not null;
    }

    /// <summary>The sidebar's status line, which opens the status menu.</summary>
    private Button? StatusLineButton() => this.FindControl<SidebarStatus>("StatusLine")?.LineButton;

    /// <summary>On the Controller page, its first focusable control: the side panel's (the
    /// inspector), else the drawing's; elsewhere, or when nothing there can take it, the status line.</summary>
    private InputElement? ControllerPageFocus()
    {
        if (_stage is not null && ReferenceEquals(CurrentPage, _stage))
        {
            var roots = new Control?[] { _stage.HardwareSidePanel, _stage };
            foreach (var root in roots)
            {
                var first = root?.GetVisualDescendants().OfType<InputElement>()
                    .FirstOrDefault(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled);
                if (CanFocus(first)) return first;
            }
        }
        return StatusLineButton();
    }

    /// <summary>Compare's card over the window while it is open.</summary>
    private void UpdateCompare()
    {
        var open = _hardware is { IsCompareOpen: true, Compare: not null };
        _compareHost.Content = open ? _hardware.Compare : null;
        _compareOverlay.IsVisible = open;
    }

    /// <summary>The sweep dialog's card over the window while it is open.</summary>
    private void UpdateCalibration()
    {
        var dialog = _hardware.Calibration;
        _calibrationHost.Content = dialog;
        _calibrationOverlay.IsVisible = dialog is not null;
    }

    /// <summary>The I2C buses dialog's card over the window while it is open.</summary>
    private void UpdateBuses()
    {
        var dialog = _hardware.Buses;
        _busesHost.Content = dialog;
        _busesOverlay.IsVisible = dialog is not null;
    }

    /// <summary>The pin dialog's card over the window while a pin field is open.</summary>
    private void UpdatePinDialog()
    {
        var field = _hardware.PinDialog;
        _pinHost.Content = field;
        _pinOverlay.IsVisible = field is not null;
    }

    /// <summary>The Connection dialog's card over the window while it is open.</summary>
    private void UpdateConnection()
    {
        var open = _hardware is { IsConnectionDialogOpen: true, ConnectionDialog: not null };
        _connectionHost.Content = open ? _hardware.ConnectionDialog : null;
        _connectionOverlay.IsVisible = open;
    }

    /// <summary>When <paramref name="overlay"/> opens, the focus moves to <paramref name="card"/>'s
    /// first focusable control (else the card itself, so Escape works while nothing in it can take
    /// the focus); when it closes, the focus goes back to where it was (the sidebar's status line),
    /// else to <paramref name="fallback"/>.</summary>
    private void TrackFocus(Border overlay, Border card, Func<InputElement?>? fallback = null)
    {
        IInputElement? before = null;
        overlay.PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (overlay.IsVisible)
            {
                before = FocusManager?.GetFocusedElement();
                DialogFocus.MoveInto(card);
            }
            else if (_progressOverlay.IsVisible && !ReferenceEquals(overlay, _progressOverlay))
            {
                // A dialog under the progress dialog closed (the leave prompt, once its save ended):
                // the focus stays in the progress dialog.
                before = null;
                DialogFocus.MoveInto(_progressCard);
            }
            else
            {
                var back = CanFocus(before) ? before : fallback?.Invoke();
                before = null;
                if (CanFocus(back)) ((InputElement)back!).Focus(NavigationMethod.Tab);
            }
        };
    }

    /// <summary>The progress dialog's next step, else its Close, takes the focus once the operation ended.</summary>
    private void FocusProgressResult()
    {
        if (!_progressOverlay.IsVisible) return;
        _progressCard.UpdateLayout();
        var target = _progressCard.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Name == "ProgressNextButton" && b is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true })
            ?? _progressCard.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "ProgressCloseButton");
        target?.Focus(NavigationMethod.Tab);
    }

    private void OnNoticesOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender)) return;   // a click inside the card
        _vm.About.CloseNoticesCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>THIRD-PARTY-NOTICES.txt and the Arimo OFL, embedded from the repository.</summary>
    internal static string LoadNotices()
    {
        static string Read(string name)
        {
            using var s = AssetLoader.Open(new Uri($"avares://Volco.App/Assets/Notices/{name}"));
            return new StreamReader(s).ReadToEnd();
        }
        return Read("THIRD-PARTY-NOTICES.txt") + "\n\n" + new string('=', 78) + "\nArimo — SIL Open Font License 1.1\n"
               + new string('=', 78) + "\n\n" + Read("OFL.txt");
    }

    private static NavItem Nav(string label, string tag, string iconKey, Func<Control> create)
    {
        Geometry? icon = null;
        if (Application.Current?.TryFindResource(iconKey, out var res) == true) icon = res as Geometry;
        return new NavItem(label, tag, icon, create);
    }

    /// <summary>The Controller page (controller-page spec §4).</summary>
    private StagePage Stage() => _stage ??= new StagePage(_vm.Controls)
    {
        Hardware = _vm.Hardware,
        HardwareSidePanel = new HardwareInspectorHost { DataContext = _vm.Hardware },
    };

    /// <summary>Switches to the page with this tag ("general" or "controller"; "controls" is the
    /// Controller page too).</summary>
    public void ShowPage(string tag)
    {
        if (tag == "controls") tag = "controller";
        var items = _navList.ItemsSource?.Cast<NavItem>().ToList() ?? new List<NavItem>();
        var index = items.FindIndex(n => n.Tag == tag);
        if (index >= 0) _navList.SelectedIndex = index;
    }

    /// <summary>The page currently shown.</summary>
    internal Control? CurrentPage => _pageHost.Content as Control;

    // Set while the selection goes back to the Controller page for the leave prompt, and while the
    // prompt's continuation leaves it.
    private bool _revertingNav;
    private bool _leaveAllowed;

    private void OnNavSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_revertingNav) return;
        var left = e.RemovedItems.OfType<NavItem>().FirstOrDefault();
        if (_navList.SelectedItem is not NavItem item) return;
        // Leaving the Controller page with unsaved changes asks first (controller-page spec §4.4);
        // the selection stays on it until the prompt is answered.
        if (left?.Tag == "controller" && !_leaveAllowed && _stage?.Hardware is { HasUnsavedChanges: true } hardware)
        {
            // The selection goes back once this change has finished (the list doesn't take a new
            // selection from inside its own change).
            Dispatcher.UIThread.Post(() =>
            {
                _revertingNav = true;
                try { _navList.SelectedItem = left; }
                finally { _revertingNav = false; }
            });
            hardware.AskToLeave(() => Navigate(item));
            return;
        }
        if (left?.Tag == "controller")
        {
            // Leaving the Controller page ends a running hardware test (hardware-page spec §8), and
            // closes its dialog, which would otherwise hold off touch-to-select.
            if (_stage?.Hardware is { } shown) shown.OnPageLeft();
            else _vm.ControllerIfCreated?.OnPageLeft();
            _vm.ControlsIfCreated?.CloseDialog();
        }
        if (!_pages.TryGetValue(item.Tag, out var page))
            _pages[item.Tag] = page = item.CreatePage();
        if (ReferenceEquals(_pageHost.Content, page)) return;
        // General has no drawing, so going to or from it crossfades the whole content area; the
        // first page shows at once.
        _pageHost.PageTransition = _pageHost.Content is null ? null : StageTransitions.PageTransition();
        _pageHost.Content = page;
    }

    /// <summary>The leave prompt's continuation: the selection moves on without asking again.</summary>
    private void Navigate(NavItem item)
    {
        _leaveAllowed = true;
        try { _navList.SelectedItem = item; }
        finally { _leaveAllowed = false; }
    }

    /// <summary>Restarts the timeout whenever a new message is shown.</summary>
    private void OnToastChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(UndoToastViewModel.Version)) return;
        _toastTimer?.Dispose();
        var version = _vm.Toast.Version;
        _toastTimer = DispatcherTimer.RunOnce(() =>
        {
            if (_vm.Toast.Version == version) _vm.Toast.Dismiss();
        }, ToastTimeout);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // Hide instead of close so the tray's Open Settings can bring the same window back.
        if (!ShouldHideInsteadOfClose(e.CloseReason, AllowClose, VolcoApplication.IsExiting)) return;
        e.Cancel = true;
        // Hiding ends a running test too, and doesn't ask about unsaved changes: the draft is kept
        // and the window reopens on the Controller page.
        _stage?.Hardware?.CloseLeavePrompt();
        _hardware.CloseCalibration();   // a sweep that hadn't ended is cancelled
        _hardware.CloseBuses();
        _hardware.ClosePinDialog();   // an open pin dialog stops its watch
        _hardware.Status.Progress.Close();   // a result goes with the window; a running operation stays
        _vm.ControllerIfCreated?.OnWindowHidden();   // and the last firmware result goes
        _vm.ControlsIfCreated?.CloseDialog();    // and closes the Controller page's dialog
        Hide();
    }

    /// <summary>Never cancel an application or OS shutdown, or the session cannot end.</summary>
    internal static bool ShouldHideInsteadOfClose(WindowCloseReason reason, bool allowClose, bool isExiting)
        => !(allowClose || isExiting
             || reason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown);
}
