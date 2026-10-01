using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Volco.App.ViewModels;

namespace Volco.App.Views.Pages;

/// <summary>
/// Slider mappings and tuning. Bindings do the work; the code-behind only configures each process
/// <see cref="AutoCompleteBox"/>: it holds the live process names (<see cref="SliderItemViewModel.GetLiveProcessNames"/>),
/// filters them on the last comma-separated token (<see cref="SliderItemViewModel.MatchesLastToken"/>), and replaces
/// that token when a suggestion is chosen.
/// </summary>
public partial class SlidersPage : UserControl
{
    public SlidersPage()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public SlidersPage(SlidersViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    /// <summary>
    /// Text the box shows after <paramref name="suggestion"/> is chosen while the user had typed
    /// <paramref name="typed"/>: earlier tokens are kept, the last one is replaced.
    /// </summary>
    internal static string ReplaceLastToken(string? typed, string? suggestion)
    {
        if (string.IsNullOrEmpty(suggestion)) return typed ?? "";
        var parts = (typed ?? "").Split(',').Select(p => p.Trim()).ToList();
        parts[^1] = suggestion;
        return string.Join(", ", parts.Where(p => p.Length > 0));
    }

    private void ProcessBox_Attached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not AutoCompleteBox box) return;

        // Built-in selection would put the bare suggestion in the box and drop earlier tokens.
        box.TextSelector = (search, item) => ReplaceLastToken(search, item);

        // The box filters the full live list itself (on the last token). Reassigning ItemsSource while
        // a suggestion is being committed clears the dropdown selection, which makes the box revert
        // the pick to the typed text. So the list is only refreshed when the user types (Populating,
        // which never fires on a commit) or the dropdown is opened by key (DropDownOpening), and only
        // when the set of names actually changed.
        box.ItemFilter = (search, item) => SliderItemViewModel.MatchesLastToken(search, item as string);
        box.Populating -= ProcessBox_Refresh;
        box.Populating += ProcessBox_Refresh;
        box.DropDownOpening -= ProcessBox_Refresh;
        box.DropDownOpening += ProcessBox_Refresh;
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
}
