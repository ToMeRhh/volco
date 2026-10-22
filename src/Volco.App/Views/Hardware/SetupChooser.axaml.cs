using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Volco.App.Views.Hardware;

/// <summary>The setup chooser over <see cref="Volco.App.ViewModels.Hardware.SetupChooserViewModel"/>; bindings do the work.</summary>
public partial class SetupChooser : UserControl
{
    public SetupChooser()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
