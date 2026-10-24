using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Volco.App.ViewModels.Hardware;

namespace Volco.App.Views.Hardware;

/// <summary>
/// The pin dialog over a <see cref="PinFieldViewModel"/>: Left/Right move the selection along a row,
/// Up/Down along a column, Enter picks it, Escape closes without change (the settings window takes
/// Escape first and does the same); a click on a pin picks it (while adding, selects it: Add or
/// Enter takes it).
/// </summary>
public partial class PinDialog : UserControl
{
    public PinDialog()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(KeyDownEvent, OnKeyDown);
    }

    private PinFieldViewModel? Vm => DataContext as PinFieldViewModel;

    private void Tile_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PinRowViewModel row }) Vm?.Pick(row);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Vm?.Cancel();

    private void Add_Click(object? sender, RoutedEventArgs e) => Vm?.Confirm();

    private void ChooseManually_Click(object? sender, RoutedEventArgs e) => Vm?.ChooseManually();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || e.Handled) return;
        switch (e.Key)
        {
            case Key.Left: vm.Move(-1); break;
            case Key.Right: vm.Move(+1); break;
            case Key.Up: vm.Move(-PinFieldViewModel.Columns); break;
            case Key.Down: vm.Move(+PinFieldViewModel.Columns); break;
            case Key.Enter when e.Source is not Button: vm.Commit(); break;
            case Key.Escape: vm.Cancel(); break;
            default: return;
        }
        e.Handled = true;
    }
}
