using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using Volco.App.Controls;
using Volco.App.ViewModels;
using Volco.Core.Input;

namespace Volco.App.Views.Pages;

/// <summary>
/// The drawing and the inspector. Bindings do the work; the code-behind selects a tile on a click,
/// Enter or Space, moves the focus between tiles with the arrow keys (spec §4.5), closes the
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
        overlay.PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (overlay.IsVisible) OnDialogOpened();
            else OnDialogClosed();
        };
    }

    // What had the focus when the dialog opened, and the dialog.
    private IInputElement? _focusBeforeDialog;
    private object? _openDialog;

    /// <summary>Focus moves into the dialog, so Escape reaches the page (a dialog may focus its
    /// own first field, such as the action dialog's search box).</summary>
    private void OnDialogOpened()
    {
        _focusBeforeDialog = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        _openDialog = Vm?.Dialog;
        var card = this.FindControl<Border>("DialogCard")!;
        card.Classes.Set("Narrow", _openDialog is TargetDialogViewModel);
        Dispatcher.UIThread.Post(() =>
        {
            if (!card.IsEffectivelyVisible || card.IsKeyboardFocusWithin) return;
            card.GetVisualDescendants().OfType<InputElement>()
                .FirstOrDefault(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled)
                ?.Focus(NavigationMethod.Tab);
        });
    }

    /// <summary>The focus goes back to the row that opened the dialog (spec §11).</summary>
    private void OnDialogClosed()
    {
        var opener = _openDialog switch
        {
            ActionDialogViewModel action => ActionRowButton(action.Gesture),
            TargetDialogViewModel => InspectorButton("ControlsRow"),
            _ => null,
        };
        var before = _focusBeforeDialog;
        _focusBeforeDialog = null;
        _openDialog = null;
        Dispatcher.UIThread.Post(() =>
        {
            if (opener is { IsEffectivelyVisible: true }) opener.Focus(NavigationMethod.Tab);
            else if (before is InputElement element && TopLevel.GetTopLevel(element) is not null) element.Focus(NavigationMethod.Tab);
        });
    }

    private Button? InspectorButton(string name)
        => this.FindControl<Border>("Inspector")?.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == name);

    /// <summary>The inspector's row button for a gesture of the selected button.</summary>
    private Button? ActionRowButton(PressType gesture)
    {
        var row = gesture switch { PressType.Double => "ActionRow_Double", PressType.Long => "ActionRow_Hold", _ => "ActionRow_Press" };
        return this.FindControl<Border>("Inspector")?.GetVisualDescendants().OfType<ContentControl>()
            .FirstOrDefault(c => c.Name == row)?.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Name == "RowButton");
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
            // Escape first closes an open dropdown in the dialog; only the next one closes the dialog.
            var open = this.FindControl<Border>("DialogCard")!.GetVisualDescendants().OfType<ComboBox>()
                .FirstOrDefault(c => c.IsDropDownOpen);
            if (open is not null) open.IsDropDownOpen = false;
            else vm.CloseDialog();
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
