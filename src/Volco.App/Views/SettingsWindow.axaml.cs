using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
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
            _controllerNav = Nav("Hardware", "controller", "Icon.Chip", () => new ControllerPage(vm.Controller)),
            Nav("Sliders", "sliders", "Icon.Sliders", () => new SlidersPage(vm.Sliders)),
            Nav("Buttons", "buttons", "Icon.Buttons", () => new ButtonsPage(vm.Buttons)),
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
    }

    private static NavItem Nav(string label, string tag, string iconKey, Func<Control> create)
    {
        Geometry? icon = null;
        if (Application.Current?.TryFindResource(iconKey, out var res) == true) icon = res as Geometry;
        return new NavItem(label, tag, icon, create);
    }

    /// <summary>Switches to the page with this tag ("general", "controller" (shown as Hardware),
    /// "sliders", "buttons").</summary>
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
        Hide();
    }

    /// <summary>Never cancel an application or OS shutdown, or the session cannot end.</summary>
    internal static bool ShouldHideInsteadOfClose(WindowCloseReason reason, bool allowClose, bool isExiting)
        => !(allowClose || isExiting
             || reason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown);
}
