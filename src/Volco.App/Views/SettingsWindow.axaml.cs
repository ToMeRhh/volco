using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Volco.App.ViewModels;
using Volco.App.Views.Pages;
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
    private readonly ContentControl _pageHost;
    private readonly Dictionary<string, Control> _pages = new();
    private IDisposable? _toastTimer;
    private readonly NavItem _controllerNav;

    /// <summary>Set by the composition root at shutdown so Closing is not cancelled.</summary>
    internal bool AllowClose { get; set; }

    public SettingsWindow(SettingsViewModel vm, VolcoRuntime runtime)
    {
        _vm = vm;
        AvaloniaXamlLoader.Load(this);
        DataContext = vm;
        _navList = this.FindControl<ListBox>("NavList")!;
        _pageHost = this.FindControl<ContentControl>("PageHost")!;

        _navList.ItemsSource = new[]
        {
            Nav("General", "general", "Icon.Settings", () => new GeneralPage(vm.General, vm.Devices)),
            Nav("Controls", "controls", "Icon.Sliders", () => new ControlsPage(vm.Controls)),
            _controllerNav = Nav("Hardware", "controller", "Icon.Chip", () => new ControllerPage(vm.Controller)),
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
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_vm.About.IsNoticesOpen) return;
        _vm.About.CloseNoticesCommand.Execute(null);
        e.Handled = true;
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

    /// <summary>Switches to the page with this tag ("general", "controls", "controller" (shown as
    /// Hardware)).</summary>
    public void ShowPage(string tag)
    {
        var items = _navList.ItemsSource?.Cast<NavItem>().ToList() ?? new List<NavItem>();
        var index = items.FindIndex(n => n.Tag == tag);
        if (index >= 0) _navList.SelectedIndex = index;
    }

    /// <summary>The page currently shown.</summary>
    internal Control? CurrentPage => _pageHost.Content as Control;

    private void OnNavSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Leaving the Controller page ends a running hardware test (spec §8.5).
        if (e.RemovedItems.OfType<NavItem>().Any(n => n.Tag == "controller")) _vm.ControllerIfCreated?.OnPageLeft();
        // Leaving the Controls page closes its dialog, which would otherwise hold off touch-to-select.
        if (e.RemovedItems.OfType<NavItem>().Any(n => n.Tag == "controls")) _vm.ControlsIfCreated?.CloseDialog();
        if (_navList.SelectedItem is not NavItem item) return;
        if (!_pages.TryGetValue(item.Tag, out var page))
            _pages[item.Tag] = page = item.CreatePage();
        _pageHost.Content = page;
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
        _vm.ControllerIfCreated?.OnPageLeft();   // hiding the window ends a running test too
        _vm.ControlsIfCreated?.CloseDialog();    // and closes the Controls page's dialog
        Hide();
    }

    /// <summary>Never cancel an application or OS shutdown, or the session cannot end.</summary>
    internal static bool ShouldHideInsteadOfClose(WindowCloseReason reason, bool allowClose, bool isExiting)
        => !(allowClose || isExiting
             || reason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown);
}
