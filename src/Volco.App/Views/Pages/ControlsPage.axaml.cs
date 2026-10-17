using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using Volco.App.Controls;
using Volco.App.ViewModels;

namespace Volco.App.Views.Pages;

/// <summary>
/// The drawing and the inspector. Bindings do the work; the code-behind selects a tile on a click,
/// Enter or Space, moves the focus between tiles with the arrow keys (spec §4.5), and closes the
/// dialog on Escape or a click on the scrim.
/// </summary>
public partial class ControlsPage : UserControl
{
    public ControlsPage()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble);
        var overlay = this.FindControl<Border>("DialogOverlay")!;
        overlay.PointerPressed += OnOverlayPressed;
        // Focus moves into an opened dialog, so Escape reaches the page.
        overlay.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && overlay.IsVisible)
                this.FindControl<Button>("CloseDialogButton")!.Focus(NavigationMethod.Tab);
        };
    }

    public ControlsPage(ControlsViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private ControlsViewModel? Vm => DataContext as ControlsViewModel;

    /// <summary>The tile <paramref name="visual"/> is in, or null.</summary>
    private static Control? TileOf(Visual? visual)
    {
        for (var v = visual; v is not null; v = v.GetVisualParent())
            if (v is SliderTile or ButtonTile) return (Control)v;
        return null;
    }

    /// <summary>Every tile in drawing order: the sliders, then the buttons.</summary>
    internal List<Control> Tiles()
        => this.GetVisualDescendants().Where(v => v is SliderTile or ButtonTile).Cast<Control>().ToList();

    private void Select(Control tile)
    {
        if (Vm is { } vm && tile.DataContext is ObservableObject item) vm.Select(item);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TileOf(e.Source as Visual) is not { } tile) return;
        Select(tile);
        tile.Focus(NavigationMethod.Pointer);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (vm.HasDialog)
        {
            if (e.Key != Key.Escape) return;
            vm.CloseDialog();
            e.Handled = true;
            return;
        }
        if (TileOf(e.Source as Visual) is not { } tile) return;
        switch (e.Key)
        {
            case Key.Enter or Key.Space:
                Select(tile);
                e.Handled = true;
                break;
            case Key.Left or Key.Up:
                MoveFocus(tile, -1);
                e.Handled = true;
                break;
            case Key.Right or Key.Down:
                MoveFocus(tile, +1);
                e.Handled = true;
                break;
        }
    }

    private void MoveFocus(Control from, int step)
    {
        var tiles = Tiles();
        var next = tiles.IndexOf(from) + step;
        if (next < 0 || next >= tiles.Count) return;
        tiles[next].Focus(NavigationMethod.Directional);
        tiles[next].BringIntoView();
    }

    private void OnOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender) || Vm is not { HasDialog: true } vm) return;   // a click inside the card
        vm.CloseDialog();
        e.Handled = true;
    }
}
