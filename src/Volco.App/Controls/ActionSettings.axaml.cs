using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Volco.App.ViewModels;
using Volco.Core.Config;
using Volco.Core.Input.Actions;

namespace Volco.App.Controls;

/// <summary>
/// The settings of the action picked in the action dialog: a device, an A/B output pair, a mode,
/// a media app, or a program or script, each field with its error underneath. DataContext is the
/// dialog's draft <see cref="ActionSlotViewModel"/>.
/// </summary>
public partial class ActionSettings : UserControl
{
    private static readonly FilePickerFileType Programs = new("Programs and shortcuts") { Patterns = new[] { "*.exe", "*.lnk", "*.url" } };
    private static readonly FilePickerFileType Scripts = new("Scripts") { Patterns = new[] { "*.bat", "*.cmd", "*.ps1", "*.py", "*.js", "*.exe" } };
    private static readonly FilePickerFileType AllFiles = new("All files") { Patterns = new[] { "*" } };

    public ActionSettings()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>The field that edits <paramref name="key"/> (an <see cref="ActionValidator"/> key).</summary>
    public Control? FieldFor(string key)
    {
        var name = key switch
        {
            ActionValidator.Device => DataContext is ActionSlotViewModel { NeedsPair: true } ? "DeviceACombo" : "DeviceCombo",
            ActionValidator.DeviceB => "DeviceBCombo",
            ActionValidator.Preset => "PresetCombo",
            ActionValidator.Path => "CommandPathBox",
            ActionValidator.WorkingDirectory => "CommandFolderBox",
            _ => null,
        };
        return name is null ? null : this.FindControl<Control>(name);
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
