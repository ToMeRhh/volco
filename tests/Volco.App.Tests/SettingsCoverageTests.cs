using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.Controls;
using Volco.App.ViewModels;
using Volco.App.ViewModels.Hardware;
using Volco.App.Views;
using Volco.App.Views.Dialogs;
using Volco.App.Views.Hardware;
using Volco.App.Views.Pages;
using Volco.App.Views.Stage;
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Media;
using Volco.Core.Runtime;
using Volco.Core.Tests.Audio;
using Volco.Core.Tests.Fakes;
using Xunit;

namespace Volco.App.Tests;

/// <summary>
/// Every setting the earlier pages edited (the UX spec's §1 inventory) is still
/// editable through the redesigned UI (sliders and buttons through the Controls page, its
/// inspector and dialogs; the controller's config and the connection through the Hardware page),
/// and what the UI writes survives a save and a fresh load.
/// Each test edits through the controls, saves, reloads config.json with a new ConfigManager and
/// checks the property.
/// </summary>
public class SettingsCoverageTests
{
    private static FakeAudioEngine Engine() => new()
    {
        Devices =
        {
            new AudioDevice("dev-1", "Speakers", AudioFlow.Render, true),
            new AudioDevice("dev-2", "Headphones", AudioFlow.Render, false),
        },
        Sessions = { new AudioSessionInfo("s1", 10, "spotify.exe", "dev-1", false) },
    };

    private static T Named<T>(Control root, string name, int index = 0) where T : Control
        => root.GetVisualDescendants().OfType<T>().Where(c => c.Name == name).ElementAt(index);

    private static Window Host(Control page)
    {
        var window = new Window { Content = page, Width = 900, Height = 1000 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static AppConfig SaveAndReload(TestRig rig)
    {
        rig.Config.Save();
        var reloaded = new ConfigManager(rig.Config.ConfigPath);
        reloaded.Load();
        return reloaded.Config;
    }

    // ── The Hardware page ─────────────────────────────────────────────────────

    private static readonly byte[] DiyBlob = ControllerConfigCodec.Encode(ControllerPresets.ApplyTo(new Volco.Core.Controller.Proto.ControllerConfig(),
        ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId)));

    /// <summary>The stage on Hardware over the DIY setup, in sync with the controller.</summary>
    /// <remarks>The page is the whole settings window, since the Connection dialog opens over it.</remarks>
    private static (Control Page, HardwareInspectorHost Host, Window Window) HardwareStage(TestRig rig, SettingsViewModel vm)
    {
        var window = new SettingsWindow(vm, rig.Runtime) { Width = 1100, Height = 900, AllowClose = true };
        window.Show();
        window.ShowPage("controller");
        var host = (HardwareInspectorHost)((StagePage)window.CurrentPage!).HardwareSidePanel!;
        rig.Connection!.SetStatus(TransportStatus.Open);
        rig.Connection.RaiseMessage(new HelloMessage(ProtocolVersion.Current, ChipFamily.Esp32, "dev", ControllerConfigState.Stored,
            ControllerConfigCodec.Crc32(DiyBlob), 5, new[] { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair }));
        Until(window, () => vm.Controller.CanEdit);
        return (window, host, window);
    }

    private static void Until(Window window, Func<bool> condition, int ms = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            Settle(window);
            if (condition()) return;
            if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("condition not met");
            Thread.Sleep(5);
        }
    }

    /// <summary>Opens a pin field's dialog and clicks GPIO <paramref name="pin"/>.</summary>
    private static void PickPin(Window window, PinField field, uint pin)
    {
        Click(Named<Button>(field, "ValueButton"));
        Settle(window);
        Click(Named<Border>(window, "PinOverlay").GetVisualDescendants().OfType<Button>()
            .First(b => b.Name == "PinTile" && ((PinRowViewModel)b.DataContext!).Pin == pin));
        Settle(window);
    }

    /// <summary>Opens a collapsed section by its header.</summary>
    private static void Open(Window window, Control root, string header, string body)
    {
        if (!Named<Control>(root, body).IsVisible) Click(Named<Button>(root, header));
        Settle(window);
    }

    private static void OtherChoice(ComboBox box) => box.SelectedIndex = box.SelectedIndex == 0 ? 1 : 0;

    [AvaloniaFact]
    public void Connection_TypePortUdpWebSocket()
    {
        using var rig = new TestRig(Engine());
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        rig.Config.Config.ControllerConfigBlob = Convert.ToBase64String(DiyBlob);
        var (page, _, window) = HardwareStage(rig, vm);
        vm.Hardware.OpenConnection(false);                                     // the card's status line
        Settle(window);

        vm.Connection.ComPort = "COM9";                                        // Connection.Serial.ComPort (no real ports on CI)
        Named<RadioButton>(page, "WifiOption").IsChecked = true;
        Settle(window);
        Named<NumericUpDown>(page, "UdpPortBox").Value = 23456;                // Connection.Udp.ListenPort
        Click(Named<Button>(page, "UseButton"));                               // Connection.Type
        Settle(window);
        Assert.Equal(ConnectionType.Udp, rig.Config.Config.Connection.Type);
        Named<RadioButton>(page, "SimulatorOption").IsChecked = true;
        Settle(window);
        Named<NumericUpDown>(page, "WebSocketPortBox").Value = 9876;           // Connection.WebSocket.Port
        Click(Named<Button>(page, "UseButton"));
        Settle(window);

        var c = SaveAndReload(rig).Connection;
        Assert.Equal(ConnectionType.WebSocket, c.Type);
        Assert.Equal("COM9", c.Serial.ComPort);
        Assert.Equal(23456, c.Udp.ListenPort);
        Assert.Equal(9876, c.WebSocket.Port);
        window.Close();
    }

    /// <summary>Every field of the controller's config is edited through the Hardware page (the pin
    /// fields, the button and bus inspectors, Controller settings and the Connection dialog's Wi-Fi
    /// tab), saved to the controller, and survives in Volco's copy (controllerConfigBlob) after a reload.</summary>
    [AvaloniaFact]
    public void Controller_EverySettingSurvivesSaveAndReload()
    {
        using var rig = new TestRig(Engine());
        rig.Config.Config.ControllerConfigBlob = Convert.ToBase64String(DiyBlob);
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var (page, host, window) = HardwareStage(rig, vm);
        var diy = ControllerConfigCodec.Decode(DiyBlob);

        vm.Controls.Select(vm.Sliders.Items[0]);
        Settle(window);
        PickPin(window, Named<PinField>(page, "PinField"), 39);                // sliders[0].pin

        vm.Controls.Select(vm.Buttons.Items[0]);
        Settle(window);
        PickPin(window, Named<PinField>(page, "PinField"), 15);                // buttons[0].pin
        PickPin(window, Named<PinField>(page, "LightPinField"), 22);           // buttons[0].led.pin
        Named<RadioButton>(page, "LitHigh").IsChecked = true;                  // buttons[0].led.active_low
        Settle(window);

        vm.Controls.Select(vm.Buttons.Items[2]);
        Settle(window);
        PickPin(window, Named<PinField>(page, "LightAPinField"), 23);          // buttons[2].led_pair.pin_a
        PickPin(window, Named<PinField>(page, "LightBPinField"), 16);          // buttons[2].led_pair.pin_b
        Named<RadioButton>(page, "LitLow").IsChecked = true;                   // buttons[2].led_pair.active_low
        Settle(window);

        vm.Controls.Select(vm.Buttons.Items[1]);
        Settle(window);
        Named<RadioButton>(page, "LightScreen").IsChecked = true;              // buttons[1].screen
        Settle(window);
        Named<ComboBox>(page, "BusBox").SelectedIndex = 1;                     // buttons[1].screen.bus
        Named<ComboBox>(page, "AddressBox").SelectedIndex = 1;                 // buttons[1].screen.address
        Settle(window);

        vm.Hardware.ShowBus(1);                                                // the button's bus link: the I2C buses dialog
        Settle(window);
        PickPin(window, Named<PinField>(window, "SdaField", 1), 17);           // i2c1.sda (bus 1's card, after bus 0's)
        PickPin(window, Named<PinField>(window, "SclField", 1), 2);            // i2c1.scl
        OtherChoice(Named<ComboBox>(window, "ClockBox", 1));                   // i2c1.clock_hz
        Click(Named<Button>(window, "BusesDoneButton"));
        Settle(window);

        Click(Named<Button>(page, "ControllerSettingsChip"));
        Settle(window);
        Open(window, host, "ScreensHeader", "ScreensBody");
        Named<Slider>(host, "BrightnessSlider").Value = 50;                    // screens.brightness
        OtherChoice(Named<ComboBox>(host, "DimAfterBox"));                     // screens.dim_enabled / dim_after_s
        OtherChoice(Named<ComboBox>(host, "DimmedToBox"));                     // screens.dim_brightness
        OtherChoice(Named<ComboBox>(host, "OffAfterBox"));                     // screens.off_enabled / off_after_s
        Named<ToggleSwitch>(host, "WakeToggle").IsChecked = false;             // screens.wake_swallows_press
        Settle(window);
        Open(window, host, "ScreensFineHeader", "ScreensFineBody");
        Named<NumericUpDown>(host, "ScrollHoldBox").Value = 900;               // screens.scroll_hold_ms
        Named<NumericUpDown>(host, "ScrollSpeedBox").Value = 40;               // screens.scroll_speed_px_s
        Named<NumericUpDown>(host, "ScrollEndHoldBox").Value = 700;            // screens.scroll_end_hold_ms
        Named<NumericUpDown>(host, "TransitionBox").Value = 300;               // screens.transition_ms
        Named<NumericUpDown>(host, "PixelShiftBox").Value = 120;               // screens.pixel_shift_every_s
        Named<ToggleSwitch>(host, "BurnInToggle").IsChecked = false;           // screens.pixel_shift_enabled
        Settle(window);

        Open(window, host, "InputHeader", "InputBody");
        Open(window, host, "InputFineHeader", "InputFineBody");
        Named<NumericUpDown>(host, "DebounceBox").Value = 25;                  // input.debounce_ms
        Named<NumericUpDown>(host, "SliderIntervalBox").Value = 15;            // input.slider_interval_ms
        Named<NumericUpDown>(host, "SmoothingBox").Value = 40;                 // input.filter_weight_pct
        Named<NumericUpDown>(host, "DeadbandBox").Value = 30;                  // input.slider_deadband
        Settle(window);

        vm.Hardware.OpenConnection(true);                                      // Controller settings › Wi-Fi
        Settle(window);
        Named<ToggleSwitch>(page, "UdpToggle").IsChecked = true;               // udp.enabled
        Settle(window);
        Named<TextBox>(page, "SsidBox").Text = "home";                         // udp.ssid
        Named<TextBox>(page, "PasswordBox").Text = "password1";                // udp.password
        Named<TextBox>(page, "PcIpBox").Text = "192.168.1.10";                 // udp.pc_ip
        Named<NumericUpDown>(page, "PcPortBox").Value = 23456;                 // udp.pc_port
        Named<NumericUpDown>(page, "LocalPortBox").Value = 23457;              // udp.local_port
        Click(Named<Button>(page, "ConnectionDone"));
        Settle(window);

        Assert.Empty(vm.Controller.Problems.Select(p => p.Message));
        var expected = ControllerConfigCodec.Encode(vm.Controller.Draft);
        Click(Named<Button>(page, "SaveButton"));                              // the save bar
        Until(window, () => rig.Connection!.SentFrames.Any(f => f.Length > 3 && f[2] == 0x88));
        var burst = rig.Connection!.SentFrames.Last(f => f.Length > 3 && f[2] == 0x88);
        rig.Connection.RaiseMessage(new ConfigResultMessage(burst[3], ConfigResultCode.Ok, 0xFF, 0xFF));
        rig.Connection.RaiseMessage(new HelloMessage(ProtocolVersion.Current, ChipFamily.Esp32, "dev", ControllerConfigState.Stored,
            ControllerConfigCodec.Crc32(expected), 5, new[] { IndicatorKind.Led, IndicatorKind.Screen, IndicatorKind.LedPair }));
        Until(window, () => !vm.Controller.IsSaving && !vm.Controller.IsDirty);

        var saved = ControllerConfigCodec.Decode(Convert.FromBase64String(SaveAndReload(rig).ControllerConfigBlob!));
        Assert.Equal(ControllerConfigCodec.Decode(expected), saved);
        Assert.Equal(39u, saved.Sliders[0].Pin);
        Assert.Equal(15u, saved.Buttons[0].Pin);
        Assert.Equal(22u, saved.Buttons[0].Led.Pin);
        Assert.NotEqual(diy.Buttons[0].Led.ActiveLow, saved.Buttons[0].Led.ActiveLow);
        Assert.Equal((23u, 16u), (saved.Buttons[2].LedPair.PinA, saved.Buttons[2].LedPair.PinB));
        Assert.NotEqual(diy.Buttons[2].LedPair.ActiveLow, saved.Buttons[2].LedPair.ActiveLow);
        Assert.Equal(1u, saved.Buttons[1].Screen.Bus);
        Assert.NotEqual(0x3Cu, saved.Buttons[1].Screen.Address);
        var bus = ControllerDraft.Bus(saved, 1)!;
        Assert.Equal((17u, 2u), (bus.Sda, bus.Scl));
        Assert.NotEqual(ControllerDraft.Bus(diy, 1)!.ClockHz, bus.ClockHz);
        var screens = saved.Screens;
        var defaults = new Volco.Core.Controller.Proto.ScreenBehaviour();
        Assert.Equal(128u, screens.Brightness);
        Assert.False(screens.DimEnabled == defaults.DimEnabled && screens.DimAfterS == defaults.DimAfterS);
        Assert.NotEqual(defaults.DimBrightness, screens.DimBrightness);
        Assert.False(screens.OffEnabled == defaults.OffEnabled && screens.OffAfterS == defaults.OffAfterS);
        Assert.False(screens.WakeSwallowsPress);
        Assert.False(screens.PixelShiftEnabled);
        Assert.Equal((900u, 40u, 700u, 300u, 120u),
                     (screens.ScrollHoldMs, screens.ScrollSpeedPxS, screens.ScrollEndHoldMs, screens.TransitionMs, screens.PixelShiftEveryS));
        Assert.Equal((25u, 15u, 40u, 30u),
                     (saved.Input.DebounceMs, saved.Input.SliderIntervalMs, saved.Input.FilterWeightPct, saved.Input.SliderDeadband));
        Assert.True(saved.Udp.Enabled);
        Assert.Equal(("home", "password1", 0xC0A8010Au, 23456u, 23457u),
                     (saved.Udp.Ssid, saved.Udp.Password, saved.Udp.PcIp, saved.Udp.PcPort, saved.Udp.LocalPort));
        window.Close();
    }

    // ── The Controls page ─────────────────────────────────────────────────────

    private static Border Inspector(StagePage page) => page.FindControl<Border>("Inspector")!;

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A click: the button's Click handlers, then its command (as Button.OnClick does).</summary>
    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (button.Command is { } command && command.CanExecute(button.CommandParameter)) command.Execute(button.CommandParameter);
    }

    /// <summary>Selects the slider and opens its target dialog from the inspector's Controls row.</summary>
    private static TargetDialog OpenTarget(StagePage page, Window window, SettingsViewModel vm, int slider)
    {
        vm.Controls.Select(vm.Sliders.Items[slider]);
        Settle(window);
        var row = Named<Button>(Inspector(page), "ControlsRow");
        row.Command!.Execute(row.CommandParameter);
        Settle(window);
        return page.GetVisualDescendants().OfType<TargetDialog>().Single();
    }

    private static void ClickTarget(TargetDialog dialog, Window window, string name)
    {
        Click(dialog.GetVisualDescendants().OfType<Button>()
            .First(b => b.Name == "TargetRowButton" && ((TargetItem)b.DataContext!).Name == name));
        Settle(window);
    }

    [AvaloniaFact]
    public void Sliders_NameTargetAndReverse()
    {
        using var rig = new TestRig(Engine());
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new StagePage(vm.Controls);
        var window = Host(page);

        // Slider 0: apps, one playing and one picked as an .exe.
        var dialog = OpenTarget(page, window, vm, 0);
        ClickTarget(dialog, window, "spotify");                                // Sliders[i].Type = Processes
        ((TargetDialogViewModel)vm.Controls.Dialog!).PickExe = () => Task.FromResult<string?>(@"C:\Program Files\VLC\vlc.exe");
        ClickTarget(dialog, window, "Other app…");                             // Sliders[i].Processes
        Click(Named<Button>(dialog, "DoneButton"));
        Settle(window);

        // Slider 1: everything else.
        dialog = OpenTarget(page, window, vm, 1);
        ClickTarget(dialog, window, "Everything else");                        // Sliders[i].Type = Unmapped
        Click(Named<Button>(dialog, "DoneButton"));
        Settle(window);

        // Slider 2: a device, a name, and every response control.
        dialog = OpenTarget(page, window, vm, 2);
        ClickTarget(dialog, window, "Headphones");                             // Sliders[i].Type = Device
        Click(Named<Button>(dialog, "DoneButton"));                            // Sliders[i].Device
        Settle(window);
        var inspector = Inspector(page);
        Named<TextBox>(inspector, "NameBox").Text = "Music";                   // Sliders[i].Name
        Named<ToggleSwitch>(inspector, "InvertSwitch").IsChecked = true;       // Sliders[i].Invert

        // Slider 3: the main output (no device).
        dialog = OpenTarget(page, window, vm, 3);
        ClickTarget(dialog, window, "Main output");
        Click(Named<Button>(dialog, "DoneButton"));
        Settle(window);

        var s = SaveAndReload(rig).Sliders;
        Assert.Equal(SliderMappingType.Processes, s[0].Type);
        Assert.Equal(new[] { "spotify.exe", "vlc.exe" }, s[0].Processes);
        Assert.Equal(SliderMappingType.Unmapped, s[1].Type);
        Assert.Equal(SliderMappingType.Device, s[2].Type);
        Assert.Equal("dev-2", s[2].Device!.Id);
        Assert.Equal("Headphones", s[2].Device!.FriendlyName);
        Assert.Equal("Music", s[2].Name);
        Assert.True(s[2].Invert);
        Assert.Equal(SliderMappingType.Device, s[3].Type);
        Assert.Null(s[3].Device);
    }

    /// <summary>Selects the button and opens the action dialog of one row ("Press", "Double", "Hold"),
    /// then picks <paramref name="action"/> from its list.</summary>
    private static ActionDialog OpenAction(StagePage page, Window window, SettingsViewModel vm, int button, string row, string action)
    {
        vm.Controls.Select(vm.Buttons.Items[button]);
        Settle(window);
        var rowButton = Named<Button>(Named<ContentControl>(Inspector(page), $"ActionRow_{row}"), "RowButton");
        rowButton.Command!.Execute(rowButton.CommandParameter);
        Settle(window);
        var dialog = page.GetVisualDescendants().OfType<ActionDialog>().Single();
        Click(dialog.GetVisualDescendants().OfType<Button>()
            .First(b => b.Name == "ActionItemButton" && ((ActionItem)b.DataContext!).Name == action));
        Settle(window);
        return dialog;
    }

    private static void Done(ActionDialog dialog, Window window, SettingsViewModel vm)
    {
        Click(Named<Button>(dialog, "DoneButton"));
        Settle(window);
        Assert.False(vm.Controls.HasDialog);   // Done accepted the action
    }

    [AvaloniaFact]
    public void Buttons_ActionsTheirSettingsAndTiming()
    {
        var media = new FakeMediaSessions();
        media.SetApps(new[] { new MediaApp("chrome", "Google Chrome", MediaPlayback.Playing) });
        var custom = new CustomActionServices(media, new FakeProgramLauncher(), new FakeProcessStarter());
        var script = $@"C:\volco-coverage-{Guid.NewGuid()}.bat";   // a Windows full path; on Linux a file by that name in the working directory
        File.WriteAllText(script, "");
        try
        {
            using var rig = new TestRig(Engine(), custom: custom);
            rig.Config.Config.Modes.Add(new ModeConfig { Id = "gaming", Name = "Gaming" });
            using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
            var page = new StagePage(vm.Controls);
            var window = Host(page);

            // Button 0: a device action on each gesture's own settings.
            var d = OpenAction(page, window, vm, 0, "Press", "Mute a device");
            Named<ComboBox>(d, "DeviceCombo").SelectedItem = "Speakers";                   // action Device
            Done(d, window, vm);
            d = OpenAction(page, window, vm, 0, "Double", "Mute microphone");
            Done(d, window, vm);
            d = OpenAction(page, window, vm, 0, "Hold", "Switch output");
            Named<ComboBox>(d, "DeviceCombo").SelectedItem = "Headphones";
            Done(d, window, vm);

            // Button 1: the toggle pair, a mode and a media app.
            d = OpenAction(page, window, vm, 1, "Press", "Toggle outputs");
            Named<ComboBox>(d, "DeviceACombo").SelectedItem = "Speakers";                  // toggle A
            Named<ComboBox>(d, "DeviceBCombo").SelectedItem = "Headphones";                // toggle B
            Done(d, window, vm);
            d = OpenAction(page, window, vm, 1, "Double", "Go to mode");
            Named<ComboBox>(d, "ModeCombo").SelectedItem = new ModeChoice("gaming", "Gaming");   // ModeId
            Done(d, window, vm);
            d = OpenAction(page, window, vm, 1, "Hold", "Play / pause");
            var apps = Named<ComboBox>(d, "MediaAppCombo");
            apps.SelectedItem = ((IEnumerable<MediaAppChoice>)apps.ItemsSource!).Single(c => c.Id == "chrome");   // MediaApp
            Done(d, window, vm);

            // Button 2: a script with every command field, and a program by link.
            d = OpenAction(page, window, vm, 2, "Press", "Run a script");
            Named<TextBox>(d, "CommandPathBox").Text = script;                             // Command.Path
            Named<TextBox>(d, "CommandArgumentsBox").Text = "on";                          // Command.Arguments
            Named<TextBox>(d, "CommandFolderBox").Text = @"C:\Scripts";                    // Command.WorkingDirectory
            Named<TextBox>(d, "CommandNameBox").Text = "Lights";                           // Command.Name
            Named<CheckBox>(d, "StartAnotherCheck").IsChecked = true;                      // WhileRunning
            Done(d, window, vm);
            d = OpenAction(page, window, vm, 2, "Double", "Open a program");
            Named<TextBox>(d, "CommandPathBox").Text = "steam://rungameid/570";
            Done(d, window, vm);

            // Timing, on button 0.
            vm.Controls.Select(vm.Buttons.Items[0]);
            Settle(window);
            Named<ToggleButton>(Inspector(page), "TimingToggle").IsChecked = true;
            Settle(window);
            Named<NumericUpDown>(Inspector(page), "LongPressBox").Value = 700;              // LongPressMs
            Named<NumericUpDown>(Inspector(page), "DoublePressBox").Value = 350;            // DoublePressWindowMs

            var b = SaveAndReload(rig).Buttons;
            var b0 = b[0].BindingFor(AppConfig.DefaultModeId);
            Assert.Equal(ButtonActionType.MuteSpecificDevice, b0.SinglePress.Action);
            Assert.Equal("dev-1", b0.SinglePress.Device!.Id);
            Assert.Equal("Speakers", b0.SinglePress.Device.FriendlyName);
            Assert.Equal(ButtonActionType.MuteCurrentInput, b0.DoublePress.Action);
            Assert.Equal(ButtonActionType.SetOutputDevice, b0.LongPress.Action);
            Assert.Equal("dev-2", b0.LongPress.Device!.Id);
            var b1 = b[1].BindingFor(AppConfig.DefaultModeId);
            Assert.Equal(ButtonActionType.ToggleOutputDevice, b1.SinglePress.Action);
            Assert.Equal("dev-1", b1.SinglePress.Device!.Id);
            Assert.Equal("dev-2", b1.SinglePress.DeviceB!.Id);
            Assert.Equal(ButtonActionType.GoToMode, b1.DoublePress.Action);
            Assert.Equal("gaming", b1.DoublePress.ModeId);
            Assert.Equal(ButtonActionType.MediaPlayPause, b1.LongPress.Action);
            Assert.Equal("chrome", b1.LongPress.MediaApp!.Id);
            Assert.Equal("Google Chrome", b1.LongPress.MediaApp.Name);
            var b2 = b[2].BindingFor(AppConfig.DefaultModeId);
            Assert.Equal(ButtonActionType.RunScript, b2.SinglePress.Action);
            Assert.Equal(script, b2.SinglePress.Command!.Path);
            Assert.Equal("on", b2.SinglePress.Command.Arguments);
            Assert.Equal(@"C:\Scripts", b2.SinglePress.Command.WorkingDirectory);
            Assert.Equal("Lights", b2.SinglePress.Command.Name);
            Assert.Equal(WhileRunningPolicy.StartAnother, b2.SinglePress.WhileRunning);
            Assert.Equal(ButtonActionType.OpenProgram, b2.DoublePress.Action);
            Assert.Equal("steam://rungameid/570", b2.DoublePress.Command!.Path);
            Assert.Equal(700, b[0].LongPressMs);
            Assert.Equal(350, b[0].DoublePressWindowMs);
        }
        finally { File.Delete(script); }
    }

    [AvaloniaFact]
    public void General_DebugLogging()
    {
        using var rig = new TestRig(Engine());
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new GeneralPage(vm.General);
        Host(page);
        try
        {
            Named<ToggleSwitch>(page, "DebugToggle").IsChecked = true;          // DebugLogging
            Assert.True(SaveAndReload(rig).DebugLogging);
        }
        finally { Volco.Core.Diagnostics.AppLog.IsDebugEnabled = false; }
    }

    [AvaloniaFact]
    public void Modes_AddRenameDelete()
    {
        using var rig = new TestRig(Engine());
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new StagePage(vm.Controls);
        Host(page);

        Named<Button>(page, "AddModeButton").Command!.Execute(null);           // Modes[] (added)
        Dispatcher.UIThread.RunJobs();
        vm.Buttons.RenameMode(vm.Buttons.EditingModeId, "Gaming");           // Modes[].Name
        vm.Buttons.RenameMode(vm.Buttons.EditingModeId, "Play");
        Assert.Equal(new[] { "Default", "Play" }, SaveAndReload(rig).Modes.Select(p => p.Name));

        vm.Buttons.MoveMode(1, 0);                                             // Modes[] (order)
        Assert.Equal(new[] { "Play", "Default" }, SaveAndReload(rig).Modes.Select(p => p.Name));
        vm.Buttons.DeleteMode(AppConfig.DefaultModeId);                      // Modes[] (removed)
        Assert.Equal(new[] { "Play" }, SaveAndReload(rig).Modes.Select(p => p.Name));
    }

    [AvaloniaFact]
    public void Devices_NicknameAndIcon()
    {
        using var rig = new TestRig(Engine());
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new StagePage(vm.Controls);
        var window = Host(page);

        // From a device row of the target dialog: ⋯ > Rename opens the device dialog.
        var dialog = OpenTarget(page, window, vm, 0);
        var menu = dialog.GetVisualDescendants().OfType<Button>()
            .First(b => b.Name == "DeviceMenuButton" && ((TargetItem)b.DataContext!).Name == "Speakers");
        ((MenuFlyout)menu.Flyout!).Items.OfType<MenuItem>().First(i => (string?)i.Header == "Rename")
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Settle(window);
        var card = page.FindControl<Border>("DeviceCard")!;

        Named<TextBox>(card, "DeviceNameBox").Text = "Desk";                                    // Devices[].Nickname
        card.GetVisualDescendants().OfType<Button>()
            .First(b => b.Name == "IconChoice" && ((IconChoiceViewModel)b.DataContext!).Icon == ScreenIcon.Headphones)
            .Command!.Execute(null);                                                            // Devices[].Icon
        Named<Button>(card, "DeviceDoneButton").Command!.Execute(null);
        Settle(window);
        Assert.False(vm.Controls.HasDeviceDialog);

        var d = SaveAndReload(rig).FindDevice("dev-1")!;
        Assert.Equal("Desk", d.Nickname);
        Assert.Equal(ScreenIcon.Headphones, d.Icon);
    }
}
