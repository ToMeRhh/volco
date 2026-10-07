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
using Volco.App.Views.Pages;
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.App.Tests;

/// <summary>
/// Review Focus #1: every setting the Step 2 pages edited (the UX spec's §1 inventory) is still
/// editable through the redesigned UI, and what the UI writes survives a save and a fresh load.
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
        var page = new ConnectionPage(vm.Connection);
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

    [AvaloniaFact]
    public void Sliders_TypeAppsDeviceAndTuning()
    {
        using var rig = new TestRig(Engine());
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new SlidersPage(vm.Sliders);
        var window = Host(page);
        foreach (var item in vm.Sliders.Items) { item.IsExpanded = true; item.IsAdvancedOpen = true; }
        Dispatcher.UIThread.RunJobs();

        // Slider 0: apps, typed into the chip box.
        Named<RadioButton>(page, "AppsOption", 0).IsChecked = true;             // Sliders[i].Type = Processes
        Dispatcher.UIThread.RunJobs();
        var box = Named<AutoCompleteBox>(page, "ProcessBox", 0);
        box.Focus();
        window.KeyTextInput("vlc.exe");
        box.IsDropDownOpen = false;
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();                                         // Sliders[i].Processes

        // Slider 1: all other apps.
        Named<RadioButton>(page, "OtherAppsOption", 1).IsChecked = true;       // Sliders[i].Type = Unmapped

        // Slider 2: a device, plus every tuning control.
        Named<RadioButton>(page, "DeviceOption", 2).IsChecked = true;          // Sliders[i].Type = Device
        Dispatcher.UIThread.RunJobs();
        Named<ComboBox>(page, "DeviceCombo", 2).SelectedItem = "Headphones";   // Sliders[i].Device
        Named<ToggleSwitch>(page, "InvertSwitch", 2).IsChecked = true;         // Sliders[i].Invert
        Named<ComboBox>(page, "CurveCombo", 2).SelectedIndex = 2;              // Sliders[i].Curve
        Named<Slider>(page, "DeadZoneSlider", 2).Value = 0.1;                  // Sliders[i].DeadZonePercent
        Named<Slider>(page, "SmoothingSlider", 2).Value = 0.5;                 // Sliders[i].SmoothingFactor

        var s = SaveAndReload(rig).Sliders;
        Assert.Equal(SliderMappingType.Processes, s[0].Type);
        Assert.Equal(new[] { "vlc.exe" }, s[0].Processes);
        Assert.Equal(SliderMappingType.Unmapped, s[1].Type);
        Assert.Equal(SliderMappingType.Device, s[2].Type);
        Assert.Equal("dev-2", s[2].Device!.Id);
        Assert.True(s[2].Invert);
        Assert.Equal(ResponseCurve.Exponential, s[2].Curve);
        Assert.Equal(0.1, s[2].DeadZonePercent);
        Assert.Equal(0.5, s[2].SmoothingFactor);
    }

    [AvaloniaFact]
    public void Buttons_ActionsDevicesTogglePairAndTiming()
    {
        using var rig = new TestRig(Engine());
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new ButtonsPage(vm.Buttons);
        Host(page);
        foreach (var item in vm.Buttons.Items) { item.IsExpanded = true; item.IsAdvancedOpen = true; }
        Dispatcher.UIThread.RunJobs();

        ActionCombo(page, "PressRow", 0).SelectedItem = ActionCatalog.For(ButtonActionType.MuteSpecificDevice);
        ActionCombo(page, "DoubleRow", 0).SelectedItem = ActionCatalog.For(ButtonActionType.MuteCurrentInput);
        ActionCombo(page, "HoldRow", 0).SelectedItem = ActionCatalog.For(ButtonActionType.SetOutputDevice);
        ActionCombo(page, "PressRow", 1).SelectedItem = ActionCatalog.For(ButtonActionType.ToggleOutputDevice);
        Dispatcher.UIThread.RunJobs();

        InRow<ComboBox>(page, "PressRow", 0, "DeviceCombo").SelectedItem = "Speakers";    // action Device
        InRow<ComboBox>(page, "HoldRow", 0, "DeviceCombo").SelectedItem = "Headphones";
        InRow<ComboBox>(page, "PressRow", 1, "DeviceACombo").SelectedItem = "Speakers";   // toggle A (was OutputDevices.Device1)
        InRow<ComboBox>(page, "PressRow", 1, "DeviceBCombo").SelectedItem = "Headphones"; // toggle B (was OutputDevices.Device2)
        Named<NumericUpDown>(page, "LongPressBox", 0).Value = 700;                        // LongPressMs
        Named<NumericUpDown>(page, "DoublePressBox", 0).Value = 350;                      // DoublePressWindowMs

        var b = SaveAndReload(rig).Buttons;
        var b0 = b[0].BindingFor(AppConfig.DefaultPresetId);
        Assert.Equal(ButtonActionType.MuteSpecificDevice, b0.SinglePress.Action);
        Assert.Equal("dev-1", b0.SinglePress.Device!.Id);
        Assert.Equal(ButtonActionType.MuteCurrentInput, b0.DoublePress.Action);
        Assert.Equal(ButtonActionType.SetOutputDevice, b0.LongPress.Action);
        Assert.Equal("dev-2", b0.LongPress.Device!.Id);
        var toggle = b[1].BindingFor(AppConfig.DefaultPresetId).SinglePress;
        Assert.Equal(ButtonActionType.ToggleOutputDevice, toggle.Action);
        Assert.Equal("dev-1", toggle.Device!.Id);
        Assert.Equal("dev-2", toggle.DeviceB!.Id);
        Assert.Equal(700, b[0].LongPressMs);
        Assert.Equal(350, b[0].DoublePressWindowMs);
    }

    private static ComboBox ActionCombo(ButtonsPage page, string row, int button)
        => InRow<ComboBox>(page, row, button, "ActionCombo");

    private static T InRow<T>(ButtonsPage page, string row, int button, string name) where T : Control
    {
        var card = Named<Border>(page, "Card", button);
        var actionRow = card.GetVisualDescendants().OfType<ActionRow>().First(r => r.Name == row);
        return actionRow.GetVisualDescendants().OfType<T>().First(c => c.Name == name);
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
    public void Devices_NicknameAndIcon()
    {
        using var rig = new TestRig(Engine());
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new DevicesPage(vm.Devices);
        Host(page);
        var item = vm.Devices.Outputs.Single(d => d.Id == "dev-1");
        item.IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        var card = page.GetVisualDescendants().OfType<Border>().First(b => b.Name == "DeviceCard" && b.DataContext == item);

        Named<TextBox>(card, "NicknameBox").Text = "Desk";                                      // Devices[].Nickname
        Named<Button>(card, "IconChoice", (int)ScreenIcon.Headphones).Command!.Execute(null);   // Devices[].Icon

        var d = SaveAndReload(rig).FindDevice("dev-1")!;
        Assert.Equal("Desk", d.Nickname);
        Assert.Equal(ScreenIcon.Headphones, d.Icon);
    }
}
