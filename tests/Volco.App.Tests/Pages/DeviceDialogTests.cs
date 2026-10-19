using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.Controls;
using Volco.App.ViewModels;
using Volco.App.Views;
using Volco.App.Views.Pages;
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.App.Tests.Pages;

/// <summary>Spec docs/design/controls-page.md §8: device nicknames and icons, edited in the device
/// dialog from the target dialog's device rows and the action dialog's device pickers. Also the
/// model tests ported from the removed General › Devices section.</summary>
public class DeviceDialogTests
{
    private const string HeadsetName = "Headset Earphone (Arctis Nova 7)";

    private sealed record Shown(ControlsPage Page, Window Window, TestRig Rig, SettingsViewModel Vm) : IDisposable
    {
        public ControlsViewModel Controls => Vm.Controls;
        public AppConfig Config => Rig.Config.Config;
        public DevicesViewModel Devices => Vm.Devices;
        public Border Card => Page.FindControl<Border>("DeviceCard")!;

        public void Dispose()
        {
            Vm.Dispose();
            Rig.Dispose();
        }
    }

    /// <summary>Speakers (the default output), a headset and a microphone (the default input).</summary>
    private static FakeAudioEngine Engine()
    {
        var speakers = new AudioDevice("spk", "Speakers (Realtek High Definition Audio)", AudioFlow.Render, true, DeviceFormFactor.Speakers);
        var mic = new AudioDevice("mic", "Microphone (Yeti Stereo Microphone)", AudioFlow.Capture, true, DeviceFormFactor.Microphone);
        return new FakeAudioEngine
        {
            Devices = { speakers, new AudioDevice("hs", HeadsetName, AudioFlow.Render, false, DeviceFormFactor.Headset), mic },
            DefaultRender = speakers, DefaultCapture = mic,
        };
    }

    /// <summary>Slider 1 is the main output, slider 2 the headset.</summary>
    private static Shown Show(Action<AppConfig>? configure = null)
    {
        var rig = new TestRig(Engine());
        var c = rig.Config.Config;
        c.Sliders = new List<SliderConfig>
        {
            new() { Type = SliderMappingType.Device },
            new() { Type = SliderMappingType.Device, Device = new DeviceRefConfig { Id = "hs", FriendlyName = HeadsetName } },
        };
        configure?.Invoke(c);
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new ControlsPage(vm.Controls);
        var window = new Window { Content = page, Width = 932, Height = 680 };
        window.Show();
        Settle(window);
        return new Shown(page, window, rig, vm);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static T In<T>(Visual root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static Control? Focused(Window window) => window.FocusManager?.GetFocusedElement() as Control;

    private static void PressKey(Window window, Key key, PhysicalKey physical)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        window.KeyRelease(key, RawInputModifiers.None, physical, null);
        Settle(window);
    }

    /// <summary>Opens the slider's target dialog from the inspector's Controls row.</summary>
    private static void OpenTarget(Shown s, int slider = 0)
    {
        s.Controls.Select(s.Vm.Sliders.Items[slider]);
        Settle(s.Window);
        var row = In<Button>(s.Page.FindControl<Border>("Inspector")!, "ControlsRow");
        row.Focus(NavigationMethod.Pointer);
        row.Command!.Execute(row.CommandParameter);
        Settle(s.Window);
    }

    /// <summary>Opens the button's Press action dialog from the inspector row.</summary>
    private static void OpenAction(Shown s, int button = 0)
    {
        s.Controls.Select(s.Vm.Buttons.Items[button]);
        Settle(s.Window);
        var row = In<Button>(In<ContentControl>(s.Page.FindControl<Border>("Inspector")!, "ActionRow_Press"), "RowButton");
        row.Focus(NavigationMethod.Pointer);
        row.Command!.Execute(row.CommandParameter);
        Settle(s.Window);
    }

    private static Button RowMenu(Shown s, string row)
        => s.Page.GetVisualDescendants().OfType<Button>()
            .First(b => b.Name == "DeviceMenuButton" && b.DataContext is TargetItem item && item.Name == row);

    private static IEnumerable<string> RowNames(Shown s)
        => s.Page.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "TargetRowButton")
            .Select(b => ((TargetItem)b.DataContext!).Name);

    /// <summary>Picks "Rename" or "Icon" in the ⋯ button's menu, as a click on the entry does.</summary>
    private static void Menu(Shown s, Button menuButton, string entry)
    {
        ((MenuFlyout)menuButton.Flyout!).Items.OfType<MenuItem>().First(i => (string?)i.Header == entry)
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Settle(s.Window);
    }

    private static DeviceDialogViewModel Dialog(Shown s) => Assert.IsType<DeviceDialogViewModel>(s.Controls.DeviceDialog);

    // ── Entry points ──────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void TargetRow_Menu_Rename_OpensDialog_NameFocused()
    {
        using var s = Show();
        OpenTarget(s);

        var menu = RowMenu(s, "Speakers");
        Assert.Equal("Device options", ToolTip.GetTip(menu));
        Assert.Equal(new[] { "Rename", "Icon" }, ((MenuFlyout)menu.Flyout!).Items.OfType<MenuItem>().Select(i => (string?)i.Header));
        // Only device rows have the menu.
        Assert.False(s.Page.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Name == "DeviceMenuButton" && ((TargetItem)b.DataContext!).Name == "Main output").IsVisible);
        Assert.True(RowMenu(s, "Microphone").IsVisible);

        Menu(s, menu, "Rename");

        Assert.True(s.Controls.HasDeviceDialog);
        Assert.True(s.Controls.HasDialog);
        Assert.Equal("Speakers", In<TextBlock>(s.Card, "DeviceTitleText").Text);
        var box = In<TextBox>(s.Card, "DeviceNameBox");
        Assert.Equal("Speakers", box.PlaceholderText);
        Assert.Same(box, Focused(s.Window));
        Assert.Equal("Windows name: Speakers (Realtek High Definition Audio) · Output", In<TextBlock>(s.Card, "DeviceFooterText").Text);
        Assert.False(In<Button>(s.Card, "ForgetButton").IsVisible);
    }

    [AvaloniaFact]
    public void Rename_ShowsInTargetDialogAndTileLabel()
    {
        using var s = Show();
        OpenTarget(s);
        Menu(s, RowMenu(s, "Headset Earphone"), "Rename");

        s.Window.KeyTextInput("Desk");
        Settle(s.Window);
        Assert.Equal("Desk", In<TextBlock>(s.Card, "DeviceTitleText").Text);   // the title follows
        In<Button>(s.Card, "DeviceDoneButton").Command!.Execute(null);
        Settle(s.Window);

        Assert.False(s.Controls.HasDeviceDialog);
        Assert.True(s.Controls.HasDialog);
        Assert.Equal("Desk", s.Config.FindDevice("hs")!.Nickname);
        Assert.Contains("Desk", RowNames(s));
        Assert.DoesNotContain("Headset Earphone", RowNames(s));
        var tile = s.Page.GetVisualDescendants().OfType<SliderTile>().ElementAt(1);
        Assert.Equal("Desk", In<TextBlock>(tile, "Label").Text);
        // The focus is back on the row's ⋯ button (the row was rebuilt with the new name).
        var focused = Assert.IsType<Button>(Focused(s.Window));
        Assert.Equal("DeviceMenuButton", focused.Name);
        Assert.Equal("device:hs", ((TargetItem)focused.DataContext!).Key);
    }

    [AvaloniaFact]
    public void UnpluggedSavedDevice_RenameWorks()
    {
        using var s = Show(c => c.Sliders[0].Device = new DeviceRefConfig { Id = "tv", FriendlyName = "LG TV (NVIDIA)" });
        OpenTarget(s);
        Menu(s, RowMenu(s, "LG TV"), "Rename");

        Assert.Equal("LG TV", Dialog(s).Title);
        Assert.Equal("Used by: Slider 1", In<TextBlock>(s.Card, "UsedByText").Text);
        Assert.True(In<TextBlock>(s.Card, "UsedByText").IsVisible);
        Assert.False(In<Button>(s.Card, "ForgetButton").IsVisible);   // used
        In<TextBox>(s.Card, "DeviceNameBox").Text = "TV";
        In<Button>(s.Card, "DeviceDoneButton").Command!.Execute(null);
        Settle(s.Window);

        var entry = s.Config.FindDevice("tv")!;
        Assert.Equal(("TV", "LG TV (NVIDIA)", AudioFlow.Render), (entry.Nickname, entry.FriendlyName, entry.Flow));
        Assert.Contains("TV", RowNames(s));
    }

    [AvaloniaFact]
    public void Icon_OpensWithIconGridFocused_PickSaves()
    {
        using var s = Show();
        OpenTarget(s);
        Menu(s, RowMenu(s, "Speakers"), "Icon");

        // The selected (automatic) icon has the focus.
        var focused = Assert.IsType<Button>(Focused(s.Window));
        Assert.Equal("IconChoice", focused.Name);
        Assert.Equal(ScreenIcon.Speaker, ((IconChoiceViewModel)focused.DataContext!).Icon);
        Assert.True(In<ItemsControl>(s.Card, "DeviceIconGrid").IsVisualAncestorOf(focused));
        Assert.Equal(10, In<ItemsControl>(s.Card, "DeviceIconGrid").ItemCount);

        s.Card.GetVisualDescendants().OfType<Button>()
            .First(b => b.Name == "IconChoice" && ((IconChoiceViewModel)b.DataContext!).Icon == ScreenIcon.Headphones)
            .Command!.Execute(null);
        Settle(s.Window);
        Assert.Equal(ScreenIcon.Headphones, s.Config.FindDevice("spk")!.Icon);
        Assert.Equal("Icon.Device.Headphones", s.Page.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Name == "TargetRowButton").Select(b => (TargetItem)b.DataContext!).First(i => i.Name == "Speakers").IconKey);

        // Reset icon goes back to the automatic one, and the entry (holding nothing) goes.
        In<Button>(s.Card, "ResetIconButton").Command!.Execute(null);
        Assert.Null(s.Config.FindDevice("spk"));
    }

    [AvaloniaFact]
    public void ActionPicker_DeviceMenu_DisabledWithoutDevice()
    {
        using var s = Show();
        OpenAction(s);
        var action = Assert.IsType<ActionDialogViewModel>(s.Controls.Dialog);
        action.Pick(ButtonActionType.MuteSpecificDevice);
        Settle(s.Window);

        var menu = In<Button>(s.Page, "DeviceMenuButton");
        Assert.Equal("Device options", ToolTip.GetTip(menu));
        Assert.False(menu.IsEffectivelyEnabled);

        action.Draft.Device = HeadsetName;
        Settle(s.Window);
        Assert.True(menu.IsEffectivelyEnabled);
        Menu(s, menu, "Rename");
        Assert.Equal("Headset Earphone", Dialog(s).Title);
        Assert.Same(In<TextBox>(s.Card, "DeviceNameBox"), Focused(s.Window));
        In<TextBox>(s.Card, "DeviceNameBox").Text = "Arctis";
        In<Button>(s.Card, "DeviceDoneButton").Command!.Execute(null);
        Settle(s.Window);
        Assert.Same(menu, Focused(s.Window));   // back on the ⋯ button
        Assert.Contains(s.Page.GetVisualDescendants().OfType<TextBlock>(), t => t.Name == "DeviceLabel" && t.Text == "Arctis");

        // Toggle outputs: each picker has its own menu, acting on its own device.
        action.Pick(ButtonActionType.ToggleOutputDevice);
        Settle(s.Window);
        var menuA = In<Button>(s.Page, "DeviceAMenuButton");
        var menuB = In<Button>(s.Page, "DeviceBMenuButton");
        Assert.False(menuA.IsEffectivelyEnabled);
        Assert.False(menuB.IsEffectivelyEnabled);
        action.Draft.DeviceB = "Speakers (Realtek High Definition Audio)";
        Settle(s.Window);
        Assert.False(menuA.IsEffectivelyEnabled);
        Assert.True(menuB.IsEffectivelyEnabled);
        Menu(s, menuB, "Icon");
        Assert.Equal("Speakers", Dialog(s).Title);
        Assert.True(Dialog(s).FocusIcon);
    }

    // ── Closing ───────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Escape_ClosesDeviceDialogFirst_ThenMain()
    {
        using var s = Show();
        OpenTarget(s);
        Menu(s, RowMenu(s, "Speakers"), "Rename");
        Assert.True(s.Controls.HasDeviceDialog);

        PressKey(s.Window, Key.Escape, PhysicalKey.Escape);
        Assert.False(s.Controls.HasDeviceDialog);
        Assert.True(s.Controls.HasDialog);
        var focused = Assert.IsType<Button>(Focused(s.Window));
        Assert.Equal("DeviceMenuButton", focused.Name);
        Assert.Equal("Speakers", ((TargetItem)focused.DataContext!).Name);

        PressKey(s.Window, Key.Escape, PhysicalKey.Escape);
        Assert.False(s.Controls.HasDialog);
    }

    [AvaloniaFact]
    public void ScrimClick_ClosesOnlyDeviceDialog()
    {
        using var s = Show();
        OpenTarget(s);
        Menu(s, RowMenu(s, "Speakers"), "Rename");

        // A click inside the card keeps it open.
        var box = In<TextBox>(s.Card, "DeviceNameBox");
        var inside = box.TranslatePoint(new Point(4, 4), s.Window)!.Value;
        s.Window.MouseDown(inside, MouseButton.Left);
        s.Window.MouseUp(inside, MouseButton.Left);
        Settle(s.Window);
        Assert.True(s.Controls.HasDeviceDialog);

        var overlay = s.Page.FindControl<Border>("DeviceOverlay")!;
        var outside = overlay.TranslatePoint(new Point(4, 4), s.Window)!.Value;
        s.Window.MouseDown(outside, MouseButton.Left);
        s.Window.MouseUp(outside, MouseButton.Left);
        Settle(s.Window);

        Assert.False(s.Controls.HasDeviceDialog);
        Assert.True(s.Controls.HasDialog);
    }

    [AvaloniaFact]
    public void ClosingTheMainDialog_ClosesTheDeviceDialog()
    {
        using var s = Show();
        OpenTarget(s);
        Menu(s, RowMenu(s, "Speakers"), "Rename");
        s.Controls.CloseDialog();
        Settle(s.Window);
        Assert.False(s.Controls.HasDeviceDialog);
        Assert.False(s.Page.FindControl<Border>("DeviceOverlay")!.IsVisible);
    }

    [AvaloniaFact]
    public void Forget_OnlyWhenUnpluggedAndUnused()
    {
        using var s = Show(c =>
        {
            c.Devices.Add(new DeviceSettingsConfig { Id = "sony", FriendlyName = "Headphones (WH-1000XM4)", Nickname = "Sony" });
            c.Sliders[0].Device = new DeviceRefConfig { Id = "tv", FriendlyName = "LG TV (NVIDIA)" };
        });
        OpenTarget(s);

        // Plugged in: no Forget.
        s.Controls.OpenDeviceDialog(new DeviceRefConfig { Id = "spk" }, AudioFlow.Render, focusIcon: false);
        Settle(s.Window);
        Assert.False(In<Button>(s.Card, "ForgetButton").IsVisible);
        s.Controls.CloseDeviceDialog();

        // Unplugged but used by slider 1: no Forget.
        s.Controls.OpenDeviceDialog(new DeviceRefConfig { Id = "tv" }, AudioFlow.Render, focusIcon: false);
        Settle(s.Window);
        Assert.False(In<Button>(s.Card, "ForgetButton").IsVisible);
        s.Controls.CloseDeviceDialog();

        // Unplugged and unused: Forget removes it and closes the dialog.
        s.Controls.OpenDeviceDialog(new DeviceRefConfig { Id = "sony" }, AudioFlow.Render, focusIcon: false);
        Settle(s.Window);
        Assert.Equal("Sony", Dialog(s).Title);
        var forget = In<Button>(s.Card, "ForgetButton");
        Assert.True(forget.IsVisible);
        Assert.Equal("Forget", forget.Content);
        forget.Command!.Execute(null);
        Settle(s.Window);
        Assert.False(s.Controls.HasDeviceDialog);
        Assert.True(s.Controls.HasDialog);
        Assert.Null(s.Config.FindDevice("sony"));
        Assert.DoesNotContain(s.Devices.Outputs, d => d.Id == "sony");
    }

    [AvaloniaFact]
    public void OpenDeviceDialog_NeedsAnOpenDialogAndAnId()
    {
        using var s = Show();
        s.Controls.OpenDeviceDialog(new DeviceRefConfig { Id = "spk" }, AudioFlow.Render, focusIcon: false);
        Assert.False(s.Controls.HasDeviceDialog);   // only from the two dialogs
        OpenTarget(s);
        s.Controls.OpenDeviceDialog(new DeviceRefConfig { FriendlyName = "Old speakers" }, AudioFlow.Render, focusIcon: false);
        Assert.False(s.Controls.HasDeviceDialog);   // a name-only reference has nothing to edit
    }

    [AvaloniaFact]
    public void GeneralPage_HasNoDevicesSection()
    {
        var rig = new TestRig(Engine());
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        using var _ = rig; using var __ = vm;
        var window = new SettingsWindow(vm, rig.Runtime) { Width = 1000, Height = 1000 };
        window.Show();
        window.ShowPage("general");
        Dispatcher.UIThread.RunJobs();

        var general = Assert.IsType<GeneralPage>(window.CurrentPage);
        var names = general.GetVisualDescendants().OfType<Control>().Select(c => c.Name).ToList();
        Assert.DoesNotContain("DevicesHost", names);
        Assert.DoesNotContain("NicknameBox", names);
        Assert.DoesNotContain("DeviceCard", names);
        Assert.DoesNotContain(general.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Devices");
        Assert.Null(typeof(GeneralPage).Assembly.GetType("Volco.App.Views.Pages.DevicesSection"));
    }

    // ── Ported from the General › Devices section (the model, DevicesViewModel) ──

    [AvaloniaFact]
    public void ListsEnabledDevices_DefaultFirst_AndGroupsByFlow()
    {
        using var s = Show();
        Assert.Equal(new[] { "Speakers", "Headset Earphone" }, s.Devices.Outputs.Select(d => d.DisplayName));
        Assert.Equal(new[] { "Microphone" }, s.Devices.Inputs.Select(d => d.DisplayName));
        Assert.Equal("Default output", s.Devices.Outputs[0].StatusText);
    }

    [AvaloniaFact]
    public void DisconnectedDevices_AppearWhenCustomizedOrReferenced()
    {
        using var s = Show(c =>
        {
            c.Devices.Add(new DeviceSettingsConfig { Id = "sony", FriendlyName = "Headphones (WH-1000XM4)", Nickname = "Sony" });
            c.Sliders[0].Device = new DeviceRefConfig { Id = "tv", FriendlyName = "LG TV (NVIDIA)" };
        });
        var sony = s.Devices.ItemFor("sony", AudioFlow.Render)!;
        var tv = s.Devices.ItemFor("tv", AudioFlow.Render)!;
        Assert.Equal(("Sony", "Unplugged", true), (sony.DisplayName, sony.StatusText, sony.CanForget));
        Assert.Equal(("LG TV", false, "Used by: Slider 1"), (tv.DisplayName, tv.CanForget, tv.UsedBy));
    }

    [AvaloniaFact]
    public void Nickname_SavesAndUpdatesTitleAndPreview()
    {
        using var s = Show();
        OpenTarget(s);
        Menu(s, RowMenu(s, "Speakers"), "Rename");
        var spk = Dialog(s).Device;

        In<TextBox>(s.Card, "DeviceNameBox").Text = "Desk speakers";

        var entry = s.Config.FindDevice("spk")!;
        Assert.Equal("Desk speakers", entry.Nickname);
        Assert.Equal("Speakers (Realtek High Definition Audio)", entry.FriendlyName);
        Assert.Equal("Desk speakers", spk.DisplayName);
        Assert.Equal("Desk speakers", Dialog(s).Title);
        Assert.Equal("Desk speakers", spk.Preview.Main);
        Assert.Equal("ACTIVE", spk.Preview.TopText);
        Assert.Null(entry.Icon);                                          // icon never chosen stays automatic
    }

    [AvaloniaFact]
    public void Icon_PickedFromTheTen_IsStored_ClearingBothRemovesTheEntry()
    {
        using var s = Show();
        var spk = s.Devices.ItemFor("spk", AudioFlow.Render)!;
        Assert.Equal(10, spk.IconChoices.Count);
        Assert.DoesNotContain(spk.IconChoices, c => c.Icon == ScreenIcon.Presets);
        Assert.Equal(ScreenIcon.Speaker, spk.Icon);                       // automatic, from the form factor

        spk.IconChoices.Single(c => c.Icon == ScreenIcon.Speakers).SelectCommand.Execute(null);
        Assert.Equal(ScreenIcon.Speakers, s.Config.FindDevice("spk")!.Icon);
        Assert.True(spk.IconChoices.Single(c => c.Icon == ScreenIcon.Speakers).IsSelected);

        spk.Nickname = "x";
        spk.Nickname = "";
        spk.ResetIconCommand.Execute(null);
        Assert.Null(s.Config.FindDevice("spk"));
    }

    [AvaloniaFact]
    public void HebrewNickname_TypesRightToLeft()
    {
        using var s = Show();
        OpenTarget(s);
        Menu(s, RowMenu(s, "Speakers"), "Rename");
        In<TextBox>(s.Card, "DeviceNameBox").Text = "רמקולים";
        Settle(s.Window);
        Assert.Equal(FlowDirection.RightToLeft, In<TextBox>(s.Card, "DeviceNameBox").FlowDirection);
    }

    [AvaloniaFact]
    public void DeviceChanges_RefreshTheLists()
    {
        using var s = Show();
        var dock = new AudioDevice("dock", "Dock Speakers", AudioFlow.Render, false);
        s.Rig.Engine.Devices.Add(dock);
        s.Rig.Engine.RaiseDeviceChanged(DeviceChangeKind.Added, dock);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(s.Devices.Outputs, d => d.Id == "dock");
    }

    [AvaloniaFact]
    public void OpeningTheDialog_RefreshesStaleReferences()
    {
        using var s = Show();
        Assert.DoesNotContain(s.Devices.Outputs, d => d.Id == "tv");

        s.Config.Sliders[1].Device = new DeviceRefConfig { Id = "tv", FriendlyName = "LG TV (NVIDIA)" };
        OpenTarget(s, 1);
        Menu(s, RowMenu(s, "LG TV"), "Rename");

        Assert.Equal("Used by: Slider 2", In<TextBlock>(s.Card, "UsedByText").Text);
        Assert.False(Dialog(s).Device.CanForget);
    }

    [AvaloniaFact]
    public void UnrelatedDeviceEvent_KeepsTheNicknameBoxBeingTyped()
    {
        using var s = Show();
        OpenTarget(s);
        Menu(s, RowMenu(s, "Speakers"), "Rename");
        var box = In<TextBox>(s.Card, "DeviceNameBox");
        box.Text = "Des";
        Settle(s.Window);

        s.Rig.Engine.RaiseDeviceChanged(DeviceChangeKind.StateChanged, s.Rig.Engine.Devices[0]);
        Settle(s.Window);

        Assert.True(s.Controls.HasDeviceDialog);
        Assert.Same(box, In<TextBox>(s.Card, "DeviceNameBox"));
        Assert.Equal("Des", box.Text);
        Assert.Same(Dialog(s).Device, s.Devices.Outputs.Single(d => d.Id == "spk"));   // the item is kept
    }
}
