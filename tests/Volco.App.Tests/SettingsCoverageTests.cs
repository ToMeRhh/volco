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
using Volco.App.Views.Dialogs;
using Volco.App.Views.Pages;
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Media;
using Volco.Core.Runtime;
using Volco.Core.Tests.Audio;
using Volco.Core.Tests.Fakes;
using Xunit;

namespace Volco.App.Tests;

/// <summary>
/// Every setting the earlier pages edited (the UX spec's §1 inventory) is still
/// editable through the redesigned UI (sliders and buttons through the Controls page, its
/// inspector and dialogs), and what the UI writes survives a save and a fresh load.
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

    [AvaloniaFact]
    public void Connection_TypePortUdpWebSocket()
    {
        using var rig = new TestRig(Engine());
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new ControllerPage(vm.Controller);   // the Controller page's Connection section
        Host(page);

        vm.Connection.ComPort = "COM9";                                        // Connection.Serial.ComPort (no real ports on CI)
        Named<RadioButton>(page, "UdpOption").IsChecked = true;                // Connection.Type
        Named<NumericUpDown>(page, "UdpPortBox").Value = 23456;                // Connection.Udp.ListenPort
        Named<RadioButton>(page, "WebSocketOption").IsChecked = true;
        Named<NumericUpDown>(page, "WebSocketPortBox").Value = 9876;           // Connection.WebSocket.Port

        var c = SaveAndReload(rig).Connection;
        Assert.Equal(ConnectionType.WebSocket, c.Type);
        Assert.Equal("COM9", c.Serial.ComPort);
        Assert.Equal(23456, c.Udp.ListenPort);
        Assert.Equal(9876, c.WebSocket.Port);
    }

    // ── The Controls page ─────────────────────────────────────────────────────

    private static Border Inspector(ControlsPage page) => page.FindControl<Border>("Inspector")!;

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>Selects the slider and opens its target dialog from the inspector's Controls row.</summary>
    private static TargetDialog OpenTarget(ControlsPage page, Window window, SettingsViewModel vm, int slider)
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
    public void Sliders_NameTargetAndResponse()
    {
        using var rig = new TestRig(Engine());
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new ControlsPage(vm.Controls);
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
        Assert.False(Named<StackPanel>(inspector, "ResponseBody").IsEffectivelyVisible);   // starts closed
        Named<ToggleButton>(inspector, "ResponseToggle").IsChecked = true;
        Settle(window);
        Named<ToggleSwitch>(inspector, "InvertSwitch").IsChecked = true;       // Sliders[i].Invert
        Named<ListBox>(inspector, "CurveList").SelectedIndex = 2;              // Sliders[i].Curve
        Named<Slider>(inspector, "DeadZoneSlider").Value = 0.1;                // Sliders[i].DeadZonePercent
        Named<Slider>(inspector, "SmoothingSlider").Value = 0.5;               // Sliders[i].SmoothingFactor

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
        Assert.Equal(ResponseCurve.Exponential, s[2].Curve);
        Assert.Equal(0.1, s[2].DeadZonePercent);
        Assert.Equal(0.5, s[2].SmoothingFactor);
        Assert.Equal(SliderMappingType.Device, s[3].Type);
        Assert.Null(s[3].Device);
    }

    /// <summary>Selects the button and opens the action dialog of one row ("Press", "Double", "Hold"),
    /// then picks <paramref name="action"/> from its list.</summary>
    private static ActionDialog OpenAction(ControlsPage page, Window window, SettingsViewModel vm, int button, string row, string action)
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
            rig.Config.Config.Presets.Add(new PresetConfig { Id = "gaming", Name = "Gaming" });
            using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
            var page = new ControlsPage(vm.Controls);
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
            Named<ComboBox>(d, "PresetCombo").SelectedItem = new PresetChoice("gaming", "Gaming");   // PresetId
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
            var b0 = b[0].BindingFor(AppConfig.DefaultPresetId);
            Assert.Equal(ButtonActionType.MuteSpecificDevice, b0.SinglePress.Action);
            Assert.Equal("dev-1", b0.SinglePress.Device!.Id);
            Assert.Equal("Speakers", b0.SinglePress.Device.FriendlyName);
            Assert.Equal(ButtonActionType.MuteCurrentInput, b0.DoublePress.Action);
            Assert.Equal(ButtonActionType.SetOutputDevice, b0.LongPress.Action);
            Assert.Equal("dev-2", b0.LongPress.Device!.Id);
            var b1 = b[1].BindingFor(AppConfig.DefaultPresetId);
            Assert.Equal(ButtonActionType.ToggleOutputDevice, b1.SinglePress.Action);
            Assert.Equal("dev-1", b1.SinglePress.Device!.Id);
            Assert.Equal("dev-2", b1.SinglePress.DeviceB!.Id);
            Assert.Equal(ButtonActionType.GoToPreset, b1.DoublePress.Action);
            Assert.Equal("gaming", b1.DoublePress.PresetId);
            Assert.Equal(ButtonActionType.MediaPlayPause, b1.LongPress.Action);
            Assert.Equal("chrome", b1.LongPress.MediaApp!.Id);
            Assert.Equal("Google Chrome", b1.LongPress.MediaApp.Name);
            var b2 = b[2].BindingFor(AppConfig.DefaultPresetId);
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
        var page = new ControlsPage(vm.Controls);
        Host(page);

        Named<Button>(page, "AddModeButton").Command!.Execute(null);           // Presets[] (added)
        Dispatcher.UIThread.RunJobs();
        vm.Buttons.RenameMode(vm.Buttons.EditingPresetId, "Gaming");           // Presets[].Name
        vm.Buttons.RenameMode(vm.Buttons.EditingPresetId, "Play");
        Assert.Equal(new[] { "Default", "Play" }, SaveAndReload(rig).Presets.Select(p => p.Name));

        vm.Buttons.MoveMode(1, 0);                                             // Presets[] (order)
        Assert.Equal(new[] { "Play", "Default" }, SaveAndReload(rig).Presets.Select(p => p.Name));
        vm.Buttons.DeleteMode(AppConfig.DefaultPresetId);                      // Presets[] (removed)
        Assert.Equal(new[] { "Play" }, SaveAndReload(rig).Presets.Select(p => p.Name));
    }

    [AvaloniaFact]
    public void Devices_NicknameAndIcon()
    {
        using var rig = new TestRig(Engine());
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new ControlsPage(vm.Controls);
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
