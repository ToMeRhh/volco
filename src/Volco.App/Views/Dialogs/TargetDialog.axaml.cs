using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
    private readonly DispatcherTimer _meters = new() { Interval = TimeSpan.FromMilliseconds(100) };

    /// <summary>The apps' level meters are being polled (while the dialog is in the window).</summary>
    internal bool MetersRunning => _meters.IsEnabled;

    public TargetDialog()
    {
        AvaloniaXamlLoader.Load(this);
        _search = this.FindControl<TextBox>("SearchBox")!;
        DialogSearch.Attach(this, _search);
        _meters.Tick += (_, _) => Vm?.PollMeters();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _meters.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _meters.Stop();
        base.OnDetachedFromVisualTree(e);
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
