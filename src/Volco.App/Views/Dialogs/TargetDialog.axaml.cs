using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Volco.App.ViewModels;

namespace Volco.App.Views.Dialogs;

/// <summary>
/// The target dialog's view. The search box has the focus on open; in it, Down and Up move through
/// the rows and Enter toggles the current one. A click on a row toggles it and gives the focus back
/// to the search box. "Other app…" opens a file picker for an .exe. Escape and a click outside are
/// the page's.
/// </summary>
public partial class TargetDialog : UserControl
{
    private static readonly FilePickerFileType Programs = new("Programs") { Patterns = new[] { "*.exe" } };

    private readonly TextBox _search;

    public TargetDialog()
    {
        AvaloniaXamlLoader.Load(this);
        _search = this.FindControl<TextBox>("SearchBox")!;
        DialogSearch.Attach(this, _search);
    }

    private TargetDialogViewModel? Vm => DataContext as TargetDialogViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm is { PickExe: null } vm) vm.PickExe = PickExeAsync;
    }

    private async Task<string?> PickExeAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return null;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose an app",
            AllowMultiple = false,
            FileTypeFilter = new[] { Programs },
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() ?? files[0].Name : null;
    }

    private void TargetRow_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || (sender as Control)?.DataContext is not TargetItem item) return;
        vm.Toggle(item);
        // The list is rebuilt, and the clicked button with it.
        _search.Focus(NavigationMethod.Pointer);
    }

    private void DoneButton_Click(object? sender, RoutedEventArgs e) => Vm?.Done();
}
