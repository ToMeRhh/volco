using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Volco.App.Controls;
using Volco.App.ViewModels;

namespace Volco.App.Views.Dialogs;

/// <summary>
/// The action dialog's view. The search box has the focus on open; in it, Down and Up move through
/// the results and Enter picks the current one. A click on an action picks it and gives the focus
/// back to the search box. Done focuses the first field with an error. Escape and a click outside
/// are the page's.
/// </summary>
public partial class ActionDialog : UserControl
{
    private readonly TextBox _search;

    public ActionDialog()
    {
        AvaloniaXamlLoader.Load(this);
        _search = this.FindControl<TextBox>("SearchBox")!;
        _search.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
    }

    private ActionDialogViewModel? Vm => DataContext as ActionDialogViewModel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() => _search.Focus(NavigationMethod.Tab));
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        switch (e.Key)
        {
            case Key.Down:
                vm.MoveSelection(+1);
                e.Handled = true;
                break;
            case Key.Up:
                vm.MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                vm.PickCurrent();
                e.Handled = true;
                break;
        }
    }

    private void ActionItem_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || (sender as Control)?.DataContext is not ActionItem item) return;
        vm.Pick(item.Type);
        // The list is rebuilt, and the clicked button with it.
        _search.Focus(NavigationMethod.Pointer);
    }

    private void DoneButton_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || vm.Done()) return;
        if (vm.Draft.FirstErrorKey is { } key && this.FindControl<ActionSettings>("Settings")?.FieldFor(key) is { } field)
            field.Focus(NavigationMethod.Tab);
    }
}
