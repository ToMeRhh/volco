using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Volco.App.ViewModels.Hardware;

namespace Volco.App.Views.Hardware;

/// <summary>The Connection dialog over <see cref="ConnectionDialogViewModel"/>; bindings do the work.</summary>
public partial class ConnectionDialog : UserControl
{
    /// <summary>The Use button's words for a tab.</summary>
    public static readonly IValueConverter UseText = new FuncValueConverter<int, string>(
        t => t switch { 1 => "Use Wi-Fi", 2 => "Use the simulator", _ => "Use USB" });

    public ConnectionDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnUse(object? sender, RoutedEventArgs e) => (DataContext as ConnectionDialogViewModel)?.UseTab();

    private void OnDone(object? sender, RoutedEventArgs e) => (DataContext as ConnectionDialogViewModel)?.Close();
}
