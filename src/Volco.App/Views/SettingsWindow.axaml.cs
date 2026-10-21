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
    private readonly Border _reinstallOverlay;
    private readonly Border _leaveOverlay;
    private readonly ContentControl _leaveHost;

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

        // The device card is in the sidebar on every page (controller-page spec §5): the actions
        // that belong to the drawing go to the Controller page first (arriving never asks to leave),
        // and its dialogs open over the whole window.
        _hardware = vm.Hardware;
        _hardware.Card.RequestController += (_, _) => ShowPage("controller");
        _hardware.PropertyChanged += OnHardwareChanged;
        _compareOverlay = this.FindControl<Border>("CompareOverlay")!;
        _compareHost = this.FindControl<ContentControl>("CompareHost")!;
        _connectionOverlay = this.FindControl<Border>("ConnectionOverlay")!;
        _connectionHost = this.FindControl<ContentControl>("ConnectionHost")!;
        _reinstallOverlay = this.FindControl<Border>("ReinstallOverlay")!;
        _leaveOverlay = this.FindControl<Border>("LeaveOverlay")!;
        _leaveHost = this.FindControl<ContentControl>("LeaveHost")!;
        foreach (var overlay in new[] { _compareOverlay, _connectionOverlay, _reinstallOverlay, _leaveOverlay })
            overlay.PointerPressed += OnScrimPressed;
        TrackFocus(_compareOverlay, this.FindControl<Border>("CompareCard")!);
        TrackFocus(_connectionOverlay, this.FindControl<Border>("ConnectionCard")!);
        // Reinstall is opened from the ⋯ menu, whose item is gone once it closes: the focus goes back to ⋯.
        TrackFocus(_reinstallOverlay, this.FindControl<Border>("ReinstallConfirm")!, CardMoreButton);
        TrackFocus(_leaveOverlay, this.FindControl<Border>("LeaveCard")!);
        UpdateCompare();
        UpdateConnection();
        UpdateLeavePrompt();
    }

    /// <summary>Whether the focus can go back to <paramref name="element"/>: shown in this window
    /// (a closed menu's item is not).</summary>
    private bool CanFocus(IInputElement? element)
        => element is InputElement { IsEffectivelyVisible: true, IsEffectivelyEnabled: true, Focusable: true } e
           && e.IsAttachedToVisualTree() && ReferenceEquals(TopLevel.GetTopLevel(e), this);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm.About.IsNoticesOpen)
        {
            if (e.Key != Key.Escape) return;
            _vm.About.CloseNoticesCommand.Execute(null);
            e.Handled = true;
            return;
        }
        // The card's dialogs and the leave prompt are modal: Escape closes the open one (the leave
        // prompt's is Stay), and no key reaches the window under it.
        var open = OpenCardOverlay();
        if (open is null) return;
        if (e.Key == Key.Escape)
        {
            CloseCardOverlay(open);
            e.Handled = true;
        }
        else if (e.Source is not Visual source || !open.IsVisualAncestorOf(source)) e.Handled = true;
    }

    /// <summary>The device card's dialog that is open, if any.</summary>
    private Border? OpenCardOverlay()
        => _leaveOverlay.IsVisible ? _leaveOverlay
         : _reinstallOverlay.IsVisible ? _reinstallOverlay
         : _connectionOverlay.IsVisible ? _connectionOverlay
         : _compareOverlay.IsVisible ? _compareOverlay
         : null;

    private void CloseCardOverlay(Border overlay)
    {
        if (ReferenceEquals(overlay, _leaveOverlay)) _hardware.CloseLeavePrompt();
        else if (ReferenceEquals(overlay, _reinstallOverlay)) _hardware.Card.Controller.CancelInstallCommand.Execute(null);
        else if (ReferenceEquals(overlay, _connectionOverlay)) _hardware.CloseConnection();
        else _hardware.CloseCompare();
    }

    /// <summary>A click on a card dialog's scrim closes it.</summary>
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
    }

    /// <summary>The leave prompt's card over the window while one is open.</summary>
    private void UpdateLeavePrompt()
    {
        var prompt = _hardware.LeavePrompt;
        _leaveHost.Content = prompt;
        _leaveOverlay.IsVisible = prompt is not null;
    }

    /// <summary>The device card's ⋯ button.</summary>
    private InputElement? CardMoreButton()
        => this.FindControl<Volco.App.Views.Hardware.DeviceCard>("Card")?.GetVisualDescendants().OfType<Button>()
               .FirstOrDefault(b => b.Name == "MoreButton");

    /// <summary>Compare's card over the window while it is open.</summary>
    private void UpdateCompare()
    {
        var open = _hardware is { IsCompareOpen: true, Compare: not null };
        _compareHost.Content = open ? _hardware.Compare : null;
        _compareOverlay.IsVisible = open;
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
    /// the focus); when it closes, the focus goes back to where it was (the card's status line),
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
            else
            {
                var back = CanFocus(before) ? before : fallback?.Invoke();
                before = null;
                if (CanFocus(back)) ((InputElement)back!).Focus(NavigationMethod.Tab);
            }
        };
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
        _vm.ControllerIfCreated?.OnPageLeft();
        _vm.ControlsIfCreated?.CloseDialog();    // and closes the Controller page's dialog
        Hide();
    }

    /// <summary>Never cancel an application or OS shutdown, or the session cannot end.</summary>
    internal static bool ShouldHideInsteadOfClose(WindowCloseReason reason, bool allowClose, bool isExiting)
        => !(allowClose || isExiting
             || reason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown);
}
