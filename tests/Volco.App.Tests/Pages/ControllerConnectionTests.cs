using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Volco.App.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Volco.App.ViewModels;
using Volco.App.Views.Pages;
using Volco.Core.Communication;
using Volco.Core.Config;
using Xunit;

namespace Volco.App.Tests.Pages;

/// <summary>The Controller page's link status and Connection section (the old Connection page).</summary>
public class ControllerConnectionTests
{
    private static (ControllerPage Page, Window Window, TestRig Rig, SettingsViewModel Vm) Show()
    {
        var rig = new TestRig();
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new ControllerPage(vm.Controller);
        var window = new Window { Content = page, Width = 900, Height = 1200 };
        window.Show();
        return (page, window, rig, vm);
    }

    private static Color ThemeColor(string key)
    {
        var app = Application.Current!;
        Assert.True(app.TryGetResource(key, app.ActualThemeVariant, out var value));
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    /// <summary>The colour of the status headline's icon.</summary>
    private static Color IconColor(ControllerPage page)
    {
        var icon = page.FindControl<StatusLine>("StatusLabel")!.GetVisualDescendants().OfType<PathIcon>().Single();
        return Assert.IsAssignableFrom<ISolidColorBrush>(icon.Foreground).Color;
    }

    [AvaloniaFact]
    public void HardwareSummary_AndVersionMismatch()
    {
        using var rig = new TestRig();
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new ControllerPage(vm.Controller);
        new Window { Content = page, Width = 900, Height = 1200 }.Show();

        rig.Connection!.SetStatus(TransportStatus.Open);
        rig.Connection.RaiseMessage(new HelloMessage(ProtocolVersion.Current, 5,
            new[] { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair }));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("5 sliders · 3 buttons (2 LED, 1 LED pair)", vm.Connection.HardwareSummary);
        Assert.False(vm.Connection.HasStatusExplanation);

        // Another protocol version: not connected, and amber rather than red.
        rig.Connection.RaiseMessage(new HelloMessage(9, 5, new[] { IndicatorKind.Led }));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Firmware update needed", vm.Connection.StatusText);
        Assert.Equal("Firmware update needed", vm.Connection.FooterText);
        Assert.StartsWith("The controller's firmware is from another version of Volco.", vm.Connection.StatusExplanation);
        Assert.Equal(LinkTone.Warning, vm.Connection.StatusTone);
        Assert.False(vm.Connection.HasHardwareSummary);
        Assert.Equal(ThemeColor("WarningBrush"),
            IconColor(page));
    }

    [AvaloniaFact]
    public void TheHardwareSummary_ShowsOnEveryTransport()
    {
        using var rig = new TestRig();
        rig.Config.Config.Connection.Type = ConnectionType.Udp;
        rig.Config.Save();
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        rig.Runtime.Reconnect();   // a UDP transport now

        rig.Connection!.SetStatus(TransportStatus.Open);
        rig.Connection.RaiseMessage(new HelloMessage(ProtocolVersion.Current, 2, new[] { IndicatorKind.Screen }));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Connected", vm.Connection.StatusText);
        Assert.Equal("Connected · Wi-Fi", vm.Connection.FooterText);
        Assert.Equal("2 sliders · 1 button (1 screen)", vm.Connection.HardwareSummary);
    }

    [AvaloniaFact]
    public void NotAnswering_IsRed_AndSaysWhatIsPluggedIn()
    {
        var (page, _, rig, vm) = Show();
        using var _ = rig;
        using var __ = vm;
        rig.Config.Config.Connection.Serial.ComPort = "COM3";
        rig.Connection!.SetStatus(TransportStatus.Open);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Connecting…", vm.Connection.StatusText);
        Assert.Equal(LinkTone.Neutral, vm.Connection.StatusTone);

        rig.Connection.SetStatus(TransportStatus.Failed);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Can't use COM3", vm.Connection.StatusText);
        Assert.StartsWith("Another app is using COM3", vm.Connection.StatusExplanation);
        Assert.Equal(LinkTone.Danger, vm.Connection.StatusTone);
        Assert.Equal(ThemeColor("DangerBrush"),
            IconColor(page));
    }

    [AvaloniaFact]
    public void Connection_StatusComesFirst_AndFollowsTheRuntime()
    {
        var (page, _, rig, vm) = Show();
        using var _ = rig;
        using var __ = vm;
        var label = page.FindControl<StatusLine>("StatusLabel")!;
        Assert.Equal("Looking for your controller", label.Text);

        rig.Connection!.SetStatus(TransportStatus.Open);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Connecting…", label.Text);
        Assert.False(page.FindControl<TextBlock>("HardwareSummaryText")!.IsVisible);   // no stale hardware

        rig.Connection.RaiseMessage(new HelloMessage(ProtocolVersion.Current, 5, new[] { IndicatorKind.Led }));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Connected", label.Text);
        Assert.Equal(ThemeColor("SuccessBrush"), IconColor(page));
        Assert.StartsWith(rig.Config.Config.Connection.Serial.ComPort, page.FindControl<TextBlock>("TransportDetails")!.Text);
    }

    [AvaloniaFact]
    public void Connection_EachTypeShowsItsOwnSettings_Visibly()
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
    public void Connection_Ports_WriteThrough()
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
    public void Connection_Reconnect_StartsAFreshTransport()
    {
        var (page, _, rig, vm) = Show();
        using var _ = rig;
        using var __ = vm;
        var first = rig.Connection;

        page.FindControl<Button>("ReconnectButton")!.Command!.Execute(null);

        Assert.NotSame(first, rig.Connection);
    }

    [AvaloniaFact]
    public void Connection_TheLinkButtons_AreInTheConnectionCard()
    {
        var (page, _, rig, vm) = Show();
        using var _ = rig;
        using var __ = vm;
        var card = page.FindControl<Border>("ConnectionPanel")!;

        foreach (var name in new[] { "ReconnectButton", "DisconnectButton", "ConnectButton" })
        {
            var button = page.FindControl<Button>(name)!;
            Assert.Contains(card, button.GetVisualAncestors());
        }
    }

    [AvaloniaFact]
    public void Connection_Disconnect_OffersConnect()
    {
        var (page, _, rig, vm) = Show();
        using var _ = rig;
        using var __ = vm;
        var reconnect = page.FindControl<Button>("ReconnectButton")!;
        var disconnect = page.FindControl<Button>("DisconnectButton")!;
        var connect = page.FindControl<Button>("ConnectButton")!;
        rig.Connection!.SetStatus(TransportStatus.Open);
        Dispatcher.UIThread.RunJobs();
        Assert.True(disconnect.IsEffectivelyVisible);
        Assert.False(connect.IsVisible);

        disconnect.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, rig.Connection.DisposeCount);
        Assert.Equal("Disconnected", page.FindControl<StatusLine>("StatusLabel")!.Text);
        Assert.Equal("Disconnected", vm.Connection.FooterText);
        Assert.StartsWith("You disconnected Volco.", vm.Connection.StatusExplanation);
        Assert.False(reconnect.IsVisible);
        Assert.False(disconnect.IsVisible);
        Assert.True(connect.IsEffectivelyVisible);

        var old = rig.Connection;
        connect.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.NotSame(old, rig.Connection);
        Assert.Equal("Looking for your controller", page.FindControl<StatusLine>("StatusLabel")!.Text);
        Assert.True(disconnect.IsEffectivelyVisible);
        Assert.False(connect.IsVisible);
    }
}
