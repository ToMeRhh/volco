using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Volco.App.Views.Hardware;

/// <summary>The leave prompt over <see cref="Volco.App.ViewModels.Hardware.LeavePromptViewModel"/>; bindings do the work.</summary>
public partial class LeavePrompt : UserControl
{
    public LeavePrompt()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
