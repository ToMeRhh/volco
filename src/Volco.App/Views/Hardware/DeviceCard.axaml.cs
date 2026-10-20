using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Volco.App.Views.Hardware;

/// <summary>The device card over <see cref="Volco.App.ViewModels.Hardware.DeviceCardViewModel"/>; bindings do the work.</summary>
public partial class DeviceCard : UserControl
{
    public DeviceCard()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
