using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Volco.App.ViewModels;

namespace Volco.App.Views.Pages;

/// <summary>
/// Presets and button cards. Bindings do the work; the code-behind wires drag-to-reorder and the
/// inline preset rename box (Enter or leaving the box commits, Escape cancels).
/// </summary>
public partial class ButtonsPage : UserControl
{
    public ButtonsPage()
    {
        AvaloniaXamlLoader.Load(this);
        var list = this.FindControl<ItemsControl>("ButtonList")!;
        Reorder = CardReorder.Attach(list, () => DataContext as IReorderableList);

        var nameBox = this.FindControl<TextBox>("PresetNameBox")!;
        nameBox.AddHandler(KeyDownEvent, OnNameBoxKeyDown, RoutingStrategies.Tunnel);
        nameBox.LostFocus += (_, _) =>
        {
            // Escape already closed the box without committing.
            if (DataContext is ButtonsViewModel { IsRenaming: true } vm) vm.CommitRenameCommand.Execute(null);
        };
        nameBox.PropertyChanged += (_, e) =>
        {
            // Opening the box focuses it with the name selected, ready to type over.
            if (e.Property == IsVisibleProperty && nameBox.IsVisible)
            {
                nameBox.Focus();
                nameBox.SelectAll();
            }
        };
    }

    public ButtonsPage(ButtonsViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    internal CardReorder Reorder { get; }

    private void OnNameBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ButtonsViewModel vm) return;
        if (e.Key == Key.Enter)
        {
            vm.CommitRenameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.IsRenaming = false;
            e.Handled = true;
        }
    }
}
