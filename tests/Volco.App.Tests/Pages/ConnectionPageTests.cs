using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using Avalonia.Threading;
using Volco.App.ViewModels;
using Volco.App.Views.Pages;
using Volco.Core.Communication;
using Xunit;

namespace Volco.App.Tests.Pages;

public class ConnectionPageTests
{
    private static (ConnectionPage Page, Window Window, TestRig Rig, SettingsViewModel Vm) Show()
    {
        var rig = new TestRig();
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new ConnectionPage(vm.Connection);
        var window = new Window { Content = page };
        window.Show();
        return (page, window, rig, vm);
    }

    [AvaloniaFact]
    public void ConnectionPage_StatusChange_UpdatesIndicator()
    {
        var (page, _, rig, vm) = Show();
        using var _ = rig;
        using var __ = vm;
        var dot = page.FindControl<Ellipse>("StatusDot")!;
        var label = page.FindControl<TextBlock>("StatusLabel")!;
        Assert.Equal("Disconnected", label.Text);

        rig.Connection!.SetStatus(ConnectionStatus.Connected);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Connected", label.Text);
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(dot.Fill);
        Assert.Equal(Color.FromArgb(0xFF, 0x22, 0xB1, 0x4C), brush.Color);
    }

    [AvaloniaFact]
    public void ConnectionPage_TypeSelection_ShowsOnlyThatTransportsFields()
    {
        var (page, _, rig, vm) = Show();
        using var _ = rig;
        using var __ = vm;
        var serial = page.FindControl<StackPanel>("SerialSection")!;
        var udp = page.FindControl<StackPanel>("UdpSection")!;
        var ws = page.FindControl<StackPanel>("WebSocketSection")!;
        var combo = page.FindControl<ComboBox>("TypeCombo")!;

        combo.SelectedIndex = 0;
        Assert.True(serial.IsVisible);
        Assert.False(udp.IsVisible);
        Assert.False(ws.IsVisible);

        combo.SelectedIndex = 1;
        Assert.False(serial.IsVisible);
        Assert.True(udp.IsVisible);
        Assert.False(ws.IsVisible);

        combo.SelectedIndex = 2;
        Assert.False(serial.IsVisible);
        Assert.False(udp.IsVisible);
        Assert.True(ws.IsVisible);
    }
}
