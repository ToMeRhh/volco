using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Volco.App.ViewModels;

namespace Volco.App.Views.Pages;

/// <summary>
/// Slider mappings and tuning. Bindings do the work; the code-behind only feeds each process
/// <see cref="AutoCompleteBox"/> its suggestions (via <see cref="SliderItemViewModel.GetProcessSuggestions"/>)
/// and replaces the last comma-separated token when a suggestion is chosen.
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
        // the pick to the typed text, so the list is only refreshed on focus / dropdown open.
        box.ItemFilter = (search, item) => SliderItemViewModel.MatchesLastToken(search, item as string);
        RefreshProcessNames(box);
        box.GotFocus -= ProcessBox_Refresh;
        box.GotFocus += ProcessBox_Refresh;
        box.DropDownOpened -= ProcessBox_Refresh;
        box.DropDownOpened += ProcessBox_Refresh;
    }

    private void ProcessBox_Refresh(object? sender, EventArgs e)
    {
        if (sender is AutoCompleteBox box) RefreshProcessNames(box);
    }

    private static void RefreshProcessNames(AutoCompleteBox box)
    {
        if (box.DataContext is not SliderItemViewModel vm) return;
        var names = vm.GetLiveProcessNames();
        // Skip no-op reassignments: they would reset the dropdown view and selection.
        if (box.ItemsSource is IEnumerable<string> current && current.SequenceEqual(names)) return;
        box.ItemsSource = names;
    }
}
