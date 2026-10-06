using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Volco.App.ViewModels;
using Volco.App.Views.Pages;
using Volco.Core.Communication;
using Volco.Core.Config;
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

    private static Color ThemeColor(string key)
    {
        var app = Application.Current!;
        Assert.True(app.TryGetResource(key, app.ActualThemeVariant, out var value));
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    [AvaloniaFact]
    public void HardwareSummary_AndVersionMismatch()
    {
        using var rig = new TestRig();
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new ConnectionPage(vm.Connection);
        new Window { Content = page }.Show();

        rig.Connection!.SetStatus(ConnectionStatus.Connected);
        rig.Connection.RaiseMessage(new HelloMessage(ProtocolVersion.Current, 5,
            new[] { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair }));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("5 sliders · 3 buttons (2 LED, 1 LED pair)", vm.Connection.HardwareSummary);
        Assert.False(vm.Connection.HasControllerError);

        rig.Connection.RaiseMessage(new HelloMessage(9, 5, new[] { IndicatorKind.Led }));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Controller firmware doesn't match this version of Volco. Reflash it.", vm.Connection.ControllerError);
        Assert.Equal("Firmware mismatch", vm.Connection.FooterText);
    }

    [AvaloniaFact]
    public void ConnectionPage_StatusComesFirst_AndFollowsTheRuntime()
    {
        var (page, _, rig, vm) = Show();
        using var _ = rig;
        using var __ = vm;
        var dot = page.FindControl<Ellipse>("StatusDot")!;
        var label = page.FindControl<TextBlock>("StatusLabel")!;
        Assert.Equal("Not connected", label.Text);

        rig.Connection!.SetStatus(ConnectionStatus.Connected);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Waiting for the controller…", label.Text);
        Assert.False(page.FindControl<TextBlock>("HardwareSummaryText")!.IsVisible);   // no stale hardware

        rig.Connection.RaiseMessage(new HelloMessage(ProtocolVersion.Current, 5, new[] { IndicatorKind.Led }));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Connected", label.Text);
        Assert.Equal(ThemeColor("SuccessBrush"), Assert.IsAssignableFrom<ISolidColorBrush>(dot.Fill).Color);
        Assert.StartsWith(rig.Config.Config.Connection.Serial.ComPort, page.FindControl<TextBlock>("TransportDetails")!.Text);
    }

    [AvaloniaFact]
    public void ConnectionPage_EachTypeShowsItsOwnSettings_Visibly()
    {
        var (page, _, rig, vm) = Show();
        using var _ = rig;
        using var __ = vm;
        var serial = page.FindControl<StackPanel>("SerialSection")!;
        var udp = page.FindControl<StackPanel>("UdpSection")!;
        var ws = page.FindControl<StackPanel>("WebSocketSection")!;

        page.FindControl<RadioButton>("SerialOption")!.IsChecked = true;
        Assert.True(serial.IsVisible);
        Assert.False(udp.IsVisible);
        Assert.False(ws.IsVisible);
        Assert.Null(page.FindControl<ComboBox>("BaudCombo"));   // fixed at 115200 since schema v5

        page.FindControl<RadioButton>("UdpOption")!.IsChecked = true;
        Assert.Equal(ConnectionType.Udp, rig.Config.Config.Connection.Type);
        Assert.False(serial.IsVisible);
        Assert.True(udp.IsVisible);
        Assert.False(ws.IsVisible);

        page.FindControl<RadioButton>("WebSocketOption")!.IsChecked = true;
        Assert.Equal(ConnectionType.WebSocket, rig.Config.Config.Connection.Type);
        Assert.False(serial.IsVisible);
        Assert.False(udp.IsVisible);
        Assert.True(ws.IsVisible);
    }

    [AvaloniaFact]
    public void ConnectionPage_Ports_WriteThrough()
    {
        var (page, _, rig, vm) = Show();
        using var _ = rig;
        using var __ = vm;

        page.FindControl<NumericUpDown>("UdpPortBox")!.Value = 23456;
        page.FindControl<NumericUpDown>("WebSocketPortBox")!.Value = 9876;

        var c = rig.Config.Config.Connection;
        Assert.Equal(23456, c.Udp.ListenPort);
        Assert.Equal(9876, c.WebSocket.Port);
    }

    [AvaloniaFact]
    public void ConnectionPage_Reconnect_StartsAFreshTransport()
    {
        var (page, _, rig, vm) = Show();
        using var _ = rig;
        using var __ = vm;
        var first = rig.Connection;

        page.FindControl<Button>("ReconnectButton")!.Command!.Execute(null);

        Assert.NotSame(first, rig.Connection);
    }

    [AvaloniaFact]
    public void ConnectionPage_Disconnect_OffersConnect()
    {
        var (page, _, rig, vm) = Show();
        using var _ = rig;
        using var __ = vm;
        var reconnect = page.FindControl<Button>("ReconnectButton")!;
        var disconnect = page.FindControl<Button>("DisconnectButton")!;
        var connect = page.FindControl<Button>("ConnectButton")!;
        rig.Connection!.SetStatus(ConnectionStatus.Connected);
        Dispatcher.UIThread.RunJobs();
        Assert.True(disconnect.IsEffectivelyVisible);
        Assert.False(connect.IsVisible);

        disconnect.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, rig.Connection.DisposeCount);
        Assert.Equal("Disconnected", page.FindControl<TextBlock>("StatusLabel")!.Text);
        Assert.Equal("Disconnected", vm.Connection.FooterText);
        Assert.False(reconnect.IsVisible);
        Assert.False(disconnect.IsVisible);
        Assert.True(connect.IsEffectivelyVisible);

        var old = rig.Connection;
        connect.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.NotSame(old, rig.Connection);
        Assert.Equal("Not connected", page.FindControl<TextBlock>("StatusLabel")!.Text);
        Assert.True(disconnect.IsEffectivelyVisible);
        Assert.False(connect.IsVisible);
    }
}
