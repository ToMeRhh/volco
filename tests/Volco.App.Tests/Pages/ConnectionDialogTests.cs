using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Time.Testing;
using Volco.App.ViewModels;
using Volco.App.ViewModels.Hardware;
using Volco.App.Views;
using Volco.App.Views.Stage;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Volco.Core.Runtime;
using Volco.Core.Tests.Runtime;
using Xunit;
using Button = Avalonia.Controls.Button;

namespace Volco.App.Tests.Pages;

/// <summary>Hardware-page spec §10: the Connection dialog, one card per transport, over the
/// settings window (controller-page spec §5).</summary>
public sealed class ConnectionDialogTests : IDisposable
{
    private const string UsbFirst = "Connect the controller over USB first; the Wi-Fi settings are sent through it.";

    public ConnectionDialogTests()
    {
        StageTransitions.Enabled = false;
        ProgressDialogViewModel.SavedHold = TimeSpan.Zero;
    }

    public void Dispose()
    {
        StageTransitions.Enabled = false;
        ProgressDialogViewModel.SavedHold = TimeSpan.FromSeconds(1);
    }

    private static readonly ControllerPreset Diy = ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId);
    private static readonly byte[] DiyBlob = ControllerConfigCodec.Encode(ControllerPresets.ApplyTo(new ControllerConfig(), Diy));
    private static readonly IndicatorKind[] DiyKinds = { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair };

    /// <summary>The DIY setup with the controller's Wi-Fi on HomeNet, sending to 192.168.1.20:12345.</summary>
    private static readonly byte[] DiyWifiBlob = Encode(c => c.Udp = new Udp
    {
        Enabled = true, Ssid = "HomeNet", Password = "password1", PcIp = 0xC0A80114, PcPort = 12345, LocalPort = 12346,
    });

    private static byte[] Encode(Action<ControllerConfig> edit)
    {
        var config = ControllerPresets.ApplyTo(new ControllerConfig(), Diy);
        edit(config);
        return ControllerConfigCodec.Encode(config);
    }

    private static readonly LocalAddress Ethernet = new(IPAddress.Parse("192.168.1.20"), "Ethernet");

    private sealed record Shown(SettingsWindow Window, TestRig Rig, SettingsViewModel Vm) : IDisposable
    {
        public ConnectionDialogViewModel Dialog => Vm.Hardware.ConnectionDialog!;

        public void Dispose()
        {
            Window.AllowClose = true;
            Window.Close();
            Vm.Dispose();
            Rig.Dispose();
        }
    }

    /// <param name="type">Volco's transport, connected (a Hello with the mirror's CRC) unless
    /// <paramref name="greet"/> is false.</param>
    /// <param name="time">The runtime's clock (the Wi-Fi waits); null: the system's.</param>
    private static Shown Show(byte[]? mirror = null, ConnectionType type = ConnectionType.Serial, FakeTimeProvider? time = null)
    {
        mirror ??= DiyBlob;
        var rig = new TestRig(options: time is null ? TestRig.NoGrace : new VolcoRuntimeOptions { ConnectGrace = TimeSpan.Zero, Time = time });
        var c = rig.Config.Config;
        c.Sliders = Enumerable.Range(0, 5).Select(i => new SliderConfig { Index = i, Type = SliderMappingType.None }).ToList();
        c.Buttons = Enumerable.Range(0, 3).Select(i => new ButtonConfig { Index = i }).ToList();
        c.ControllerConfigBlob = Convert.ToBase64String(mirror);
        if (type != c.Connection.Type)
        {
            c.Connection.Type = type;
            rig.Runtime.Reconnect();
        }
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var window = new SettingsWindow(vm, rig.Runtime) { Width = 1100, Height = 720 };
        window.Show();
        window.ShowPage("controller");
        vm.Hardware.ReadAddresses = () => new[] { Ethernet };
        Greet(rig.Connection!, ControllerConfigCodec.Crc32(mirror));
        Until(window, () => vm.Controller.CanEdit);
        return new Shown(window, rig, vm);
    }

    private static HelloMessage StoredHello(uint crc)
        => new(ProtocolVersion.Current, ChipFamily.Esp32, "dev", ControllerConfigState.Stored, crc, 5, DiyKinds);

    private static void Greet(FakeControllerConnection c, uint crc)
    {
        c.SetStatus(TransportStatus.Open);
        c.RaiseMessage(StoredHello(crc));
    }

    private static bool IsWrite(byte[] f) => f.Length > 3 && f[2] == 0x88;

    /// <summary>The blob of a ConfigWrite burst (begin, chunks, end).</summary>
    private static byte[] WrittenBlob(byte[] burst)
    {
        var length = (burst[4] << 8) | burst[5];
        var blob = new List<byte>();
        var at = burst[1] + 3;
        while (at < burst.Length && blob.Count < length)
        {
            var len = burst[at + 1];
            if (burst[at + 2] == 0x89) blob.AddRange(burst.Skip(at + 2 + 4).Take(len - 4));
            at += len + 3;
        }
        return blob.ToArray();
    }

    /// <summary>The fake controller: waits for a config write on <paramref name="c"/>, answers OK and
    /// restarts with a Hello of the written blob's CRC; returns the blob.</summary>
    private static byte[] AnswerWrite(Shown s, FakeControllerConnection c)
    {
        Until(s.Window, () => c.SentFrames.Any(IsWrite));
        var burst = c.SentFrames.Last(IsWrite);
        var blob = WrittenBlob(burst);
        c.RaiseMessage(new ConfigResultMessage(burst[3], ConfigResultCode.Ok, 0xFF, 0xFF));
        c.RaiseMessage(StoredHello(ControllerConfigCodec.Crc32(blob)));
        return blob;
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

    /// <summary>A click: the button's Click handlers, then its command (as Button.OnClick does).</summary>
    private static void Click(Shown s, string name)
    {
        var button = In<Button>(s.Window, name);
        Assert.True(button.IsEffectivelyVisible, $"{name} is hidden");
        Assert.True(button.IsEffectivelyEnabled, $"{name} is disabled");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (button.Command is { } command && command.CanExecute(button.CommandParameter)) command.Execute(button.CommandParameter);
        Settle(s.Window);
    }

    private static Shown Opened(bool wifi = false, byte[]? mirror = null, ConnectionType type = ConnectionType.Serial, FakeTimeProvider? time = null)
    {
        var s = Show(mirror, type, time);
        s.Vm.Hardware.OpenConnection(wifi);
        Settle(s.Window);
        return s;
    }

    private static bool Visible(Shown s, string name) => In<Control>(s.Window, name).IsEffectivelyVisible;

    // ── The cards ────────────────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void OpensOnCardInUse()
    {
        using var s = Opened();
        Assert.True(In<Border>(s.Window, "ConnectionOverlay").IsVisible);
        Assert.Equal("How Volco reaches the controller", In<TextBlock>(s.Window, "ConnectionTitle").Text);
        Assert.Equal(new[] { "USB cable", "Wi-Fi", "Simulator" }, s.Dialog.Cards.Select(c => c.Title));
        Assert.True(Visible(s, "UsbCardBody"));
        Assert.False(Visible(s, "WifiCardBody"));
        Assert.False(Visible(s, "SimulatorCardBody"));

        s.Vm.Hardware.CloseConnection();
        s.Vm.Connection.ConnectionType = ConnectionType.WebSocket;
        s.Vm.Hardware.OpenConnection(false);
        Settle(s.Window);
        Assert.True(Visible(s, "SimulatorCardBody"));
        Assert.False(Visible(s, "UsbCardBody"));
    }

    [AvaloniaFact]
    public void OpensOnWifiFromControllerSettings()
    {
        using var s = Show();
        s.Vm.Hardware.SelectControllerSettings();
        Settle(s.Window);
        Assert.Equal("Off", In<TextBlock>(s.Window, "WifiSummary").Text);
        Click(s, "WifiHeader");

        Assert.True(s.Vm.Hardware.IsConnectionDialogOpen);
        Assert.True(s.Dialog.WifiCard.IsOpen);
        Assert.True(Visible(s, "WifiCardBody"));
        Assert.False(Visible(s, "UsbCardBody"));
        Assert.Equal(ConnectionType.Serial, s.Rig.Config.Config.Connection.Type);
    }

    [AvaloniaFact]
    public void OpeningCardSwitchesNothing()
    {
        using var s = Opened();
        Click(s, "WifiCardHeader");
        Assert.True(Visible(s, "WifiCardBody"));
        Assert.False(Visible(s, "UsbCardBody"));
        Assert.True(Visible(s, "UsbCardSummary"));   // a closed card shows its summary
        Click(s, "SimulatorCardHeader");
        Assert.True(Visible(s, "SimulatorCardBody"));
        Assert.False(Visible(s, "WifiCardBody"));
        Settle(s.Window);
        Assert.Equal(ConnectionType.Serial, s.Rig.Config.Config.Connection.Type);
        Assert.Equal(ConnectionType.Serial, s.Vm.Connection.ConnectionType);
    }

    [AvaloniaFact]
    public void InUseBadgeOnlyOnCurrent()
    {
        using var s = Opened();
        Assert.True(Visible(s, "UsbInUse"));
        Assert.False(Visible(s, "WifiInUse"));
        Assert.False(Visible(s, "SimulatorInUse"));
        Assert.Equal("In use", In<TextBlock>(s.Window, "UsbInUse").Text);
        Assert.False(Visible(s, "SwitchToUsbButton"));   // nothing to switch to in the card in use

        Click(s, "SimulatorCardHeader");
        Assert.Equal("Switch to the simulator", In<Button>(s.Window, "SwitchToSimulatorButton").Content);
        Click(s, "SwitchToSimulatorButton");
        Assert.Equal(ConnectionType.WebSocket, s.Rig.Config.Config.Connection.Type);
        Assert.True(Visible(s, "SimulatorInUse"));
        Assert.False(Visible(s, "UsbInUse"));
        Assert.False(Visible(s, "SwitchToSimulatorButton"));

        // From the simulator, Switch to USB switches at once.
        Click(s, "UsbCardHeader");
        Assert.Equal("Switch to USB", In<Button>(s.Window, "SwitchToUsbButton").Content);
        Click(s, "SwitchToUsbButton");
        Assert.False(Visible(s, "WifiOffQuestion"));
        Assert.Equal(ConnectionType.Serial, s.Rig.Config.Config.Connection.Type);
        Assert.True(Visible(s, "UsbInUse"));
    }

    [AvaloniaFact]
    public void EditsInCardNotInUseDroppedOnClose()
    {
        using var s = Opened();
        var port = s.Rig.Config.Config.Connection.WebSocket.Port;
        Click(s, "SimulatorCardHeader");
        In<NumericUpDown>(s.Window, "WebSocketPortBox").Value = 9999;
        Click(s, "WifiCardHeader");
        In<TextBox>(s.Window, "SsidBox").Text = "Elsewhere";
        Click(s, "SimulatorCardHeader");
        Assert.Equal(9999m, In<NumericUpDown>(s.Window, "WebSocketPortBox").Value);   // kept while the dialog is open
        Assert.Equal(port, s.Rig.Config.Config.Connection.WebSocket.Port);

        Click(s, "ConnectionClose");
        Assert.False(s.Vm.Hardware.IsConnectionDialogOpen);
        Assert.Equal(port, s.Rig.Config.Config.Connection.WebSocket.Port);

        s.Vm.Hardware.OpenConnection(false);
        Settle(s.Window);
        Click(s, "SimulatorCardHeader");
        Assert.Equal((decimal)port, In<NumericUpDown>(s.Window, "WebSocketPortBox").Value);
        Click(s, "WifiCardHeader");
        Assert.Equal("", In<TextBox>(s.Window, "SsidBox").Text);
    }

    [AvaloniaFact]
    public void EditsInTheCardInUseApplyAtOnce()
    {
        using var s = Opened(type: ConnectionType.WebSocket);
        Assert.True(Visible(s, "SimulatorCardBody"));
        In<NumericUpDown>(s.Window, "WebSocketPortBox").Value = 9999;
        Settle(s.Window);
        Assert.Equal(9999, s.Rig.Config.Config.Connection.WebSocket.Port);
        Assert.Equal("Port 9999", s.Dialog.SimulatorCard.Summary);
    }

    [AvaloniaFact]
    public void EscapeClosesAndCloseCloses()
    {
        using var s = Show();
        var overlay = In<Border>(s.Window, "ConnectionOverlay");
        s.Vm.Hardware.OpenConnection(false);
        Settle(s.Window);
        Assert.True(overlay.IsVisible);
        s.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Settle(s.Window);
        Assert.False(s.Vm.Hardware.IsConnectionDialogOpen);
        Assert.False(overlay.IsVisible);

        s.Vm.Hardware.OpenConnection(false);
        Settle(s.Window);
        Assert.Equal("Close", In<Button>(s.Window, "ConnectionClose").Content);
        Click(s, "ConnectionClose");
        Assert.False(overlay.IsVisible);

        s.Vm.Hardware.OpenConnection(false);
        Settle(s.Window);
        s.Window.MouseDown(new Point(4, 4), MouseButton.Left);
        s.Window.MouseUp(new Point(4, 4), MouseButton.Left);
        Settle(s.Window);
        Assert.False(s.Vm.Hardware.IsConnectionDialogOpen);
    }

    [AvaloniaFact]
    public void UnsavedChangesPromptBeforeOpening()
    {
        using var s = Show();
        s.Vm.Controller.DebounceMs = 25;
        Assert.True(s.Vm.Hardware.HasUnsavedControllerChanges);

        s.Vm.Hardware.OpenConnection(false);
        Settle(s.Window);
        Assert.False(s.Vm.Hardware.IsConnectionDialogOpen);
        Assert.NotNull(s.Vm.Hardware.LeavePrompt);
        Assert.Equal("Your controller changes aren't saved. Save them before opening Connection?",
                     In<TextBlock>(s.Window, "LeaveMessage").Text);
        Assert.Equal("Discard and continue", In<Button>(s.Window, "DiscardAndContinueButton").Content);
        Assert.Equal("Stay", In<Button>(s.Window, "StayButton").Content);

        Click(s, "DiscardAndContinueButton");
        Assert.Null(s.Vm.Hardware.LeavePrompt);
        Assert.False(s.Vm.Hardware.HasUnsavedControllerChanges);
        Assert.True(s.Vm.Hardware.IsConnectionDialogOpen);
        Assert.True(In<Border>(s.Window, "ConnectionOverlay").IsVisible);
    }

    // ── USB ──────────────────────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void SwitchToUsbFromWifiAsks()
    {
        using var s = Opened(mirror: DiyWifiBlob, type: ConnectionType.Udp);
        Assert.True(Visible(s, "WifiInUse"));
        Assert.True(Visible(s, "WifiCardBody"));
        Assert.Equal("Save", In<Button>(s.Window, "WifiSaveButton").Content);
        var udp = s.Rig.Connection!;

        Click(s, "UsbCardHeader");
        Click(s, "SwitchToUsbButton");
        Assert.Equal("Plug the controller in, then switch. Volco turns its Wi-Fi off.", In<TextBlock>(s.Window, "WifiOffQuestion").Text);
        Assert.Equal("Switch to USB and turn Wi-Fi off", In<Button>(s.Window, "ConfirmSwitchButton").Content);
        Click(s, "CancelSwitchButton");
        Assert.False(Visible(s, "WifiOffQuestion"));
        Settle(s.Window);
        Assert.Equal(ConnectionType.Udp, s.Rig.Config.Config.Connection.Type);

        Click(s, "SwitchToUsbButton");
        Click(s, "ConfirmSwitchButton");
        Until(s.Window, () => s.Rig.Connection != udp && s.Rig.Connection!.Config.Type == ConnectionType.Serial);
        Until(s.Window, () => s.Dialog.UsbCard.IsInUse);
        Assert.Equal(ConnectionType.Serial, s.Rig.Config.Config.Connection.Type);
        Assert.False(s.Vm.Hardware.Status.Progress.IsOpen);   // nothing is saved before the USB Hello

        var usb = s.Rig.Connection!;
        Greet(usb, ControllerConfigCodec.Crc32(DiyWifiBlob));
        Until(s.Window, () => s.Vm.Hardware.Status.Progress.IsRunning);
        var sent = AnswerWrite(s, usb);
        Assert.False(ControllerConfigCodec.Decode(sent).Udp.Enabled);
        Until(s.Window, () => !s.Vm.Hardware.Status.Progress.IsOpen && s.Dialog.WifiCard.Summary == "Off");
        Assert.Equal(ConnectionType.Serial, s.Rig.Config.Config.Connection.Type);
    }

    // ── Wi-Fi ────────────────────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void NoNumberedHeadings()
    {
        using var s = Opened(wifi: true);
        Click(s, "PortsHeader");
        Assert.True(Visible(s, "ListenPortBox"));
        var texts = In<Control>(s.Window, "WifiCardBody").GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? "").Where(t => t.Length > 0).ToList();
        Assert.Contains("This PC's address", texts);
        Assert.Contains("Sent over USB, then Volco switches to Wi-Fi.", texts);
        Assert.DoesNotContain(texts, t => Regex.IsMatch(t, @"^\d+\.\s"));
        Assert.Equal(Ethernet.Label, ((LocalAddress)In<ComboBox>(s.Window, "AddressCombo").SelectedItem!).Label);
        Assert.Equal("192.168.1.20 · Ethernet", Ethernet.Label);
    }

    [AvaloniaFact]
    public void SaveAndSwitchDisabledWithoutUsb()
    {
        using var s = Opened(wifi: true);
        In<TextBox>(s.Window, "SsidBox").Text = "HomeNet";
        In<TextBox>(s.Window, "PasswordBox").Text = "password1";
        Settle(s.Window);
        Assert.True(In<Button>(s.Window, "WifiSaveButton").IsEffectivelyEnabled);
        Assert.False(Visible(s, "WifiDisabledReason"));

        s.Rig.Connection!.SetStatus(TransportStatus.Closed);
        Until(s.Window, () => !s.Vm.Connection.IsConnected);
        Assert.False(In<Button>(s.Window, "WifiSaveButton").IsEffectivelyEnabled);
        Assert.Equal(UsbFirst, s.Dialog.WifiCard.DisabledReason);
        Assert.Equal(UsbFirst, In<TextBlock>(s.Window, "WifiDisabledReason").Text);
    }

    [AvaloniaFact]
    public void WifiProblemsShowOnTheCard()
    {
        using var s = Opened(wifi: true);
        In<TextBox>(s.Window, "SsidBox").Text = "HomeNet";
        In<TextBox>(s.Window, "PasswordBox").Text = "short";
        Settle(s.Window);
        Assert.False(s.Dialog.WifiCard.CanSave);
        Assert.Equal("The password must be 8 to 64 characters, or empty for an open network.", s.Dialog.WifiCard.Error);
        Assert.True(Visible(s, "WifiErrorText"));
        Assert.False(In<Button>(s.Window, "WifiSaveButton").IsEffectivelyEnabled);
        In<TextBox>(s.Window, "PasswordBox").Text = "password1";
        Settle(s.Window);
        Assert.Null(s.Dialog.WifiCard.Error);
        Assert.True(s.Dialog.WifiCard.CanSave);
    }

    /// <summary>Fills the Wi-Fi card with HomeNet and clicks Save and switch; the controller saves
    /// over USB and restarts, and Volco moves to Wi-Fi. Returns the USB transport.</summary>
    private static FakeControllerConnection SaveAndSwitch(Shown s, out byte[] sent)
    {
        In<TextBox>(s.Window, "SsidBox").Text = "HomeNet";
        In<TextBox>(s.Window, "PasswordBox").Text = "password1";
        Settle(s.Window);
        Assert.Equal("Save and switch", In<Button>(s.Window, "WifiSaveButton").Content);
        var usb = s.Rig.Connection!;
        Click(s, "WifiSaveButton");
        Assert.True(s.Vm.Hardware.Status.Progress.IsRunning);
        sent = AnswerWrite(s, usb);
        Until(s.Window, () => s.Rig.Connection != usb && s.Rig.Connection!.Config.Type == ConnectionType.Udp);
        Until(s.Window, () => s.Vm.Hardware.Status.Progress.Step?.StartsWith("Waiting for the controller over Wi-Fi… ") == true);
        Assert.Equal("Waiting for the controller over Wi-Fi… 45 s", s.Vm.Hardware.Status.Progress.Step);
        return usb;
    }

    [AvaloniaFact]
    public void SaveAndSwitchStaysOnWifi()
    {
        var time = new FakeTimeProvider();
        using var s = Opened(wifi: true, time: time);
        SaveAndSwitch(s, out var sent);
        var udp = ControllerConfigCodec.Decode(sent).Udp;
        Assert.Equal(("HomeNet", "password1", 0xC0A80114u), (udp.Ssid, udp.Password, udp.PcIp));

        Greet(s.Rig.Connection!, ControllerConfigCodec.Crc32(sent));
        Until(s.Window, () => s.Dialog.WifiCard.IsInUse && !s.Dialog.WifiCard.IsOpen && !s.Vm.Hardware.Status.Progress.IsOpen);
        Assert.Equal(ConnectionType.Udp, s.Rig.Config.Config.Connection.Type);
        Assert.True(Visible(s, "WifiInUse"));
        Assert.False(Visible(s, "UsbInUse"));
        Assert.Equal("On HomeNet · sending to 192.168.1.20", In<TextBlock>(s.Window, "WifiCardSummary").Text);
        Assert.Null(s.Dialog.WifiCard.Error);
    }

    [AvaloniaFact]
    public void SaveAndSwitchFallsBackWithMessage()
    {
        var time = new FakeTimeProvider();
        using var s = Opened(wifi: true, time: time);
        SaveAndSwitch(s, out _);

        time.Advance(WifiSwitch.FallbackWindow + WifiSwitch.AnswerWait + TimeSpan.FromSeconds(1));
        Until(s.Window, () => s.Rig.Config.Config.Connection.Type == ConnectionType.Serial && !s.Vm.Hardware.Status.Progress.IsOpen);
        Until(s.Window, () => s.Dialog.UsbCard.IsInUse);
        Assert.True(Visible(s, "WifiCardBody"));
        Assert.Equal("The controller didn't answer over Wi-Fi. Check the network name and password.",
                     In<Volco.App.Controls.StatusLine>(s.Window, "WifiErrorText").Text);
    }

    [AvaloniaFact]
    public void SaveOverWifiFallsBackWithMessage()
    {
        var time = new FakeTimeProvider();
        using var s = Opened(mirror: DiyWifiBlob, type: ConnectionType.Udp, time: time);
        Assert.Equal("HomeNet", In<TextBox>(s.Window, "SsidBox").Text);   // filled from the saved config
        Assert.Equal("password1", In<TextBox>(s.Window, "PasswordBox").Text);
        In<TextBox>(s.Window, "SsidBox").Text = "Office";
        Click(s, "PortsHeader");
        In<NumericUpDown>(s.Window, "ListenPortBox").Value = 12400;
        Settle(s.Window);
        Assert.Equal("Save", In<Button>(s.Window, "WifiSaveButton").Content);
        Assert.False(Visible(s, "WifiNote"));
        var old = s.Rig.Connection!;
        Click(s, "WifiSaveButton");
        Until(s.Window, () => old.SentFrames.Any(IsWrite));
        var burst = old.SentFrames.Last(IsWrite);
        old.RaiseMessage(new ConfigResultMessage(burst[3], ConfigResultCode.Ok, 0xFF, 0xFF));
        Until(s.Window, () => s.Rig.Connection!.Config.Udp.ListenPort == 12400);   // Volco moves with the controller
        var moved = s.Rig.Connection!;
        Until(s.Window, () => s.Vm.Hardware.Status.Progress.Step?.StartsWith("Waiting for the controller over Wi-Fi…") == true);

        time.Advance(WifiSwitch.FallbackWindow + TimeSpan.FromSeconds(1));   // no Hello on the new settings
        Until(s.Window, () => s.Rig.Connection != moved && s.Rig.Connection!.Config.Udp.ListenPort == 12345);
        Greet(s.Rig.Connection!, ControllerConfigCodec.Crc32(DiyWifiBlob));   // back on the old settings
        Until(s.Window, () => !s.Vm.Hardware.Status.Progress.IsOpen && s.Dialog.WifiCard.Error is not null);
        Assert.Equal("The controller couldn't use the new Wi-Fi settings and went back to the old ones. Check the network name and password.",
                     In<Volco.App.Controls.StatusLine>(s.Window, "WifiErrorText").Text);
        Assert.Equal(ConnectionType.Udp, s.Rig.Config.Config.Connection.Type);
    }
}
