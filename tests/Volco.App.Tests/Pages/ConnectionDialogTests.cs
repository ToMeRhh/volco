using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.ViewModels;
using Volco.App.Views.Stage;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Volco.Core.Tests.Runtime;
using Xunit;
using Button = Avalonia.Controls.Button;

namespace Volco.App.Tests.Pages;

/// <summary>Hardware-page spec §10: the Connection dialog, a modal card over the stage.</summary>
public sealed class ConnectionDialogTests : IDisposable
{
    public ConnectionDialogTests() => StageTransitions.Enabled = false;

    public void Dispose() => StageTransitions.Enabled = false;

    private static readonly ControllerPreset Diy = ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId);
    private static readonly byte[] DiyBlob = ControllerConfigCodec.Encode(ControllerPresets.ApplyTo(new ControllerConfig(), Diy));
    private static readonly IndicatorKind[] DiyKinds = { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair };

    private sealed record Shown(StagePage Page, Window Window, TestRig Rig, SettingsViewModel Vm) : IDisposable
    {
        public void Dispose()
        {
            Window.Close();
            Vm.Dispose();
            Rig.Dispose();
        }
    }

    private static Shown Show()
    {
        var rig = new TestRig();
        var c = rig.Config.Config;
        c.Sliders = Enumerable.Range(0, 5).Select(i => new SliderConfig { Index = i, Type = SliderMappingType.None }).ToList();
        c.Buttons = Enumerable.Range(0, 3).Select(i => new ButtonConfig { Index = i }).ToList();
        c.ControllerConfigBlob = Convert.ToBase64String(DiyBlob);
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new StagePage(vm.Controls) { Hardware = vm.Hardware };
        var window = new Window { Content = page, Width = 1100, Height = 720 };
        window.Show();
        page.Mode = StageMode.Hardware;
        rig.Connection!.SetStatus(TransportStatus.Open);
        rig.Connection.RaiseMessage(new HelloMessage(ProtocolVersion.Current, ChipFamily.Esp32, "dev", ControllerConfigState.Stored,
                                                     ControllerConfigCodec.Crc32(DiyBlob), 5, DiyKinds));
        Until(window, () => vm.Controller.CanEdit);
        return new Shown(page, window, rig, vm);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Until(Window window, Func<bool> condition, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            Settle(window);
            if (condition()) return;
            if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("condition not met");
            Thread.Sleep(5);
        }
    }

    private static T In<T>(Visual root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [AvaloniaFact]
    public void SegmentSelectsTheTabAndUseSwitchesTheTransport()
    {
        using var s = Show();
        s.Vm.Hardware.OpenConnection(false);
        Settle(s.Window);
        Assert.True(In<Border>(s.Page, "ConnectionOverlay").IsVisible);
        Assert.Equal("Connection", In<TextBlock>(s.Page, "ConnectionTitle").Text);
        Assert.True(In<StackPanel>(s.Page, "UsbSection").IsVisible);
        Assert.True(In<TextBlock>(s.Page, "InUseNote").IsVisible);

        In<RadioButton>(s.Page, "WifiOption").IsChecked = true;
        Settle(s.Window);
        Assert.Equal(ConnectionType.Serial, s.Rig.Config.Config.Connection.Type);
        Assert.True(In<StackPanel>(s.Page, "WifiSection").IsVisible);
        Assert.False(In<StackPanel>(s.Page, "UsbSection").IsVisible);
        Assert.False(In<TextBlock>(s.Page, "InUseNote").IsVisible);
        var use = In<Button>(s.Page, "UseButton");
        Assert.Equal("Use Wi-Fi", use.Content);

        use.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle(s.Window);
        Assert.Equal(ConnectionType.Udp, s.Rig.Config.Config.Connection.Type);
        Assert.True(In<TextBlock>(s.Page, "InUseNote").IsVisible);

        In<RadioButton>(s.Page, "SimulatorOption").IsChecked = true;
        Settle(s.Window);
        Assert.Equal(ConnectionType.Udp, s.Rig.Config.Config.Connection.Type);
        Assert.True(In<StackPanel>(s.Page, "SimulatorSection").IsVisible);
        Assert.Equal("Use the simulator", In<Button>(s.Page, "UseButton").Content);
    }

    [AvaloniaFact]
    public void OpenedFromWifiShowsTheWifiTab()
    {
        using var s = Show();
        s.Vm.Hardware.OpenConnection(true);
        Settle(s.Window);
        Assert.True(In<StackPanel>(s.Page, "WifiSection").IsVisible);
        Assert.False(In<StackPanel>(s.Page, "UsbSection").IsVisible);
        Assert.Equal(ConnectionType.Serial, s.Rig.Config.Config.Connection.Type);
    }

    [AvaloniaFact]
    public void ControllerWifiFieldsEditTheDraft()
    {
        using var s = Show();
        s.Vm.Controller.UdpEnabled = true;
        s.Vm.Hardware.OpenConnection(true);
        Settle(s.Window);
        var saved = s.Rig.Config.Config.ControllerConfigBlob;
        s.Vm.Controller.Ssid = "";

        In<TextBox>(s.Page, "SsidBox").Text = "HomeNet";
        Settle(s.Window);

        Assert.Equal("HomeNet", s.Vm.Controller.Ssid);
        Assert.True(s.Vm.Controller.IsDirty);
        Assert.Equal(saved, s.Rig.Config.Config.ControllerConfigBlob);
    }

    [AvaloniaFact]
    public void EscapeClosesAndDoneCloses()
    {
        using var s = Show();
        var overlay = In<Border>(s.Page, "ConnectionOverlay");
        s.Vm.Hardware.OpenConnection(false);
        Settle(s.Window);
        Assert.True(overlay.IsVisible);
        s.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Settle(s.Window);
        Assert.False(s.Vm.Hardware.IsConnectionDialogOpen);
        Assert.False(overlay.IsVisible);

        s.Vm.Hardware.OpenConnection(false);
        Settle(s.Window);
        In<Button>(s.Page, "ConnectionDone").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle(s.Window);
        Assert.False(overlay.IsVisible);

        s.Vm.Hardware.OpenConnection(false);
        Settle(s.Window);
        s.Window.MouseDown(new Point(4, 4), MouseButton.Left);
        s.Window.MouseUp(new Point(4, 4), MouseButton.Left);
        Settle(s.Window);
        Assert.False(s.Vm.Hardware.IsConnectionDialogOpen);
    }
}
