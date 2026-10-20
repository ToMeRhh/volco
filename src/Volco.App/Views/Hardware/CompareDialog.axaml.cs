using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Volco.App.Views.Hardware;

/// <summary>Compare over <see cref="Volco.App.ViewModels.Hardware.CompareViewModel"/>; bindings do the work.</summary>
public partial class CompareDialog : UserControl
{
    public CompareDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
