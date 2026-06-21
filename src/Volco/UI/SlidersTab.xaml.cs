using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Volco.UI.ViewModels;

namespace Volco.UI;

/// <summary>
/// Settings tab for slider mappings and tuning.
/// Thin code-behind: OnNavigatedTo casts the navigation parameter to SettingsViewModel,
/// and AutoSuggestBox handlers populate suggestion lists by delegating to the item VM.
/// All other UI state is driven by two-way x:Bind to SliderItemViewModel.
/// </summary>
public sealed partial class SlidersTab : Page
{
    public SlidersViewModel? ViewModel { get; private set; }

    public SlidersTab()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not SettingsViewModel settingsVm)
            return;

        ViewModel = settingsVm.Sliders;
        Bindings.Update();
    }

    // ── AutoSuggestBox handlers (suggestion population only) ────────────────

    private void ProcessBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;

        if (sender.DataContext is SliderItemViewModel vm)
        {
            var suggestions = vm.GetProcessSuggestions(sender.Text);
            sender.ItemsSource = suggestions.Count > 0 ? suggestions : null;
        }
    }

    private void ProcessBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        var suggestion = args.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(suggestion))
            return;

        // Replace the last typed token with the chosen suggestion, preserving earlier tokens.
        var parts = sender.Text.Split(',').Select(p => p.Trim()).ToList();
        if (parts.Count > 0)
            parts[parts.Count - 1] = suggestion;
        else
            parts.Add(suggestion);

        // Suppress the TextChanged/UserInput feedback loop by setting Text directly;
        // x:Bind TwoWay will propagate the final value back to ProcessesText.
        sender.Text = string.Join(", ", parts.Where(p => !string.IsNullOrEmpty(p)));
    }
}
