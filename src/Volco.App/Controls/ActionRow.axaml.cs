using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Volco.App.ViewModels;
using Volco.Core.Config;

namespace Volco.App.Controls;

/// <summary>
/// One action row of a button card: label, grouped action picker, Test, and the chosen action's
/// settings (a device, an A/B output pair, a preset, a media app, or a program or script)
/// indented underneath. DataContext is an <see cref="ViewModels.ActionSlotViewModel"/>.
/// </summary>
public partial class ActionRow : UserControl
{
    private static readonly FilePickerFileType Programs = new("Programs and shortcuts") { Patterns = new[] { "*.exe", "*.lnk", "*.url" } };
    private static readonly FilePickerFileType Scripts = new("Scripts") { Patterns = new[] { "*.bat", "*.cmd", "*.ps1", "*.py", "*.js", "*.exe" } };
    private static readonly FilePickerFileType AllFiles = new("All files") { Patterns = new[] { "*" } };

    public ActionRow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void DeviceCombo_ContainerPrepared(object? sender, ContainerPreparedEventArgs e)
        => Converters.DeviceLabelConverter.Stamp(sender, e);

    /// <summary>Picks the program or script file; the path box takes its local path.</summary>
    private async void BrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ActionSlotViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var script = vm.SelectedChoice.Type == ButtonActionType.RunScript;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = script ? "Choose a script" : "Choose a program",
            AllowMultiple = false,
            FileTypeFilter = new[] { script ? Scripts : Programs, AllFiles },
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) vm.CommandPath = path;
    }
}
