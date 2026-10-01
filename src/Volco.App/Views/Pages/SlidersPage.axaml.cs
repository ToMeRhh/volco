using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Volco.App.ViewModels;

namespace Volco.App.Views.Pages;

/// <summary>
/// Slider cards. Bindings do most of the work; the code-behind wires drag-to-reorder and the app
/// chip input: an <see cref="AutoCompleteBox"/> suggesting live audio sessions, where Enter or a
/// picked suggestion adds a chip and clears the box.
/// </summary>
public partial class SlidersPage : UserControl
{
    public SlidersPage()
    {
        AvaloniaXamlLoader.Load(this);
        var list = this.FindControl<ItemsControl>("SliderList")!;
        Reorder = CardReorder.Attach(list, () => DataContext as IReorderableList);
    }

    public SlidersPage(SlidersViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    internal CardReorder Reorder { get; }

    private void ProcessBox_Attached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not AutoCompleteBox box) return;

        // The box filters the full live list itself. Reassigning ItemsSource while a suggestion is
        // being committed clears the dropdown selection and reverts the pick, so the list is only
        // refreshed when the user types (Populating, which never fires on a commit) or opens the
        // dropdown by key, and only when the set of names actually changed.
        box.Populating -= ProcessBox_Refresh;
        box.Populating += ProcessBox_Refresh;
        box.DropDownOpening -= ProcessBox_Refresh;
        box.DropDownOpening += ProcessBox_Refresh;
        box.DropDownClosed -= ProcessBox_DropDownClosed;
        box.DropDownClosed += ProcessBox_DropDownClosed;
        box.RemoveHandler(KeyDownEvent, ProcessBox_KeyDown);
        box.AddHandler(KeyDownEvent, ProcessBox_KeyDown, RoutingStrategies.Tunnel);
    }

    private void ProcessBox_Refresh(object? sender, EventArgs e)
    {
        if (sender is AutoCompleteBox box) RefreshProcessNames(box);
    }

    private static void RefreshProcessNames(AutoCompleteBox box)
    {
        if (box.DataContext is not SliderItemViewModel vm) return;
        var names = vm.GetLiveProcessNames();
        // An unchanged set must never reassign: that would reset the dropdown view and selection.
        if (box.ItemsSource is IEnumerable<string> current
            && new HashSet<string>(current, StringComparer.OrdinalIgnoreCase).SetEquals(names)) return;
        box.ItemsSource = names;
    }

    /// <summary>A suggestion was picked (keyboard or pointer): it becomes a chip.</summary>
    private void ProcessBox_DropDownClosed(object? sender, EventArgs e)
    {
        if (sender is not AutoCompleteBox box || box.SelectedItem is not string picked) return;
        Commit(box, picked);
    }

    /// <summary>Enter with the dropdown closed adds whatever was typed.</summary>
    private void ProcessBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not AutoCompleteBox box || box.IsDropDownOpen) return;
        if (string.IsNullOrWhiteSpace(box.Text)) return;
        Commit(box, box.Text);
        e.Handled = true;
    }

    private static void Commit(AutoCompleteBox box, string? app)
    {
        if (box.DataContext is not SliderItemViewModel vm) return;
        vm.AddApp(app);
        // After the box has finished its own commit, so it does not put the text back.
        Dispatcher.UIThread.Post(() =>
        {
            box.SelectedItem = null;
            box.Text = "";
        });
    }
}
