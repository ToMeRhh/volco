using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Volco.App.ViewModels.Hardware;

namespace Volco.App.Views.Hardware;

/// <summary>The Connection dialog over <see cref="ConnectionDialogViewModel"/>; bindings do the work.</summary>
public partial class ConnectionDialog : UserControl
{
    public ConnectionDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
