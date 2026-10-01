using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Volco.App.ViewModels;
using Volco.App.Views.Pages;
using Volco.Core.Runtime;

namespace Volco.App.Views;

public sealed record NavItem(string Label, string Tag, Geometry? Icon, Func<Control> CreatePage);

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;
    private readonly VolcoRuntime _runtime;
    private readonly ListBox _navList;
    private readonly ContentControl _pageHost;
    private readonly Dictionary<string, Control> _pages = new();

    /// <summary>Set by the composition root at shutdown so Closing is not cancelled.</summary>
    internal bool AllowClose { get; set; }

    public SettingsWindow(SettingsViewModel vm, VolcoRuntime runtime)
    {
        _vm = vm;
        _runtime = runtime;
        AvaloniaXamlLoader.Load(this);
        _navList = this.FindControl<ListBox>("NavList")!;
        _pageHost = this.FindControl<ContentControl>("PageHost")!;

        _navList.ItemsSource = new[]
        {
            Nav("Connection", "connection", "Icon.Connection", () => new ConnectionPage(vm.Connection)),
            Nav("Sliders", "sliders", "Icon.Sliders", () => new SlidersPage()),
            Nav("Buttons", "buttons", "Icon.Buttons", () => new ButtonsPage()),
            Nav("Output Devices", "outputdevices", "Icon.Speaker", () => new OutputDevicesPage()),
            Nav("General", "general", "Icon.Settings", () => new GeneralPage(vm.General)),
        };
        _navList.SelectionChanged += OnNavSelectionChanged;
        _navList.SelectedIndex = 0;

        Closing += OnClosing;

        // Rule (f): the firmware only reports a slider when it moves, so seed live levels now and
        // whenever the (hidden/shown, never recreated) window is activated again.
        _vm.Sliders.SeedLiveLevels(_runtime.GetSliderLevel);
        Activated += (_, _) => _vm.Sliders.SeedLiveLevels(_runtime.GetSliderLevel);
    }

    private static NavItem Nav(string label, string tag, string iconKey, Func<Control> create)
    {
        Geometry? icon = null;
        if (Application.Current?.TryFindResource(iconKey, out var res) == true) icon = res as Geometry;
        return new NavItem(label, tag, icon, create);
    }

    private void OnNavSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_navList.SelectedItem is not NavItem item) return;
        if (!_pages.TryGetValue(item.Tag, out var page))
            _pages[item.Tag] = page = item.CreatePage();
        _pageHost.Content = page;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // Hide instead of close so the tray's Open Settings can bring the same window back.
        if (AllowClose || VolcoApplication.IsExiting) return;
        e.Cancel = true;
        Hide();
    }
}
