using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Volco.App.Views.Hardware;

/// <summary>The progress dialog over <see cref="Volco.App.ViewModels.Hardware.ProgressDialogViewModel"/>; bindings do the work.</summary>
public partial class ProgressDialog : UserControl
{
    public ProgressDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
