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
        // Built-in selection would put the bare suggestion in the box and drop earlier tokens.
        if (sender is AutoCompleteBox box)
            box.TextSelector = (search, item) => ReplaceLastToken(search, item);
    }

    private void ProcessBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is AutoCompleteBox { DataContext: SliderItemViewModel vm } box)
        {
            var suggestions = vm.GetProcessSuggestions(box.Text ?? "");
            box.ItemsSource = suggestions.Count > 0 ? suggestions : null;
        }
    }
}
