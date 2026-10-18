using Avalonia;
using Avalonia.Controls;
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
using Volco.Core.Config;
using Volco.Core.Input;
using Volco.Core.Media;
using Volco.Core.Runtime;
using Volco.Core.Tests.Audio;
using Volco.Core.Tests.Fakes;
using Xunit;

namespace Volco.App.Tests.Pages;

/// <summary>Spec docs/design/controls-page.md §6.1, §6.3, §6.4 and §11: the action dialog.</summary>
public class ActionDialogTests
{
    private static readonly DeviceRefConfig Speakers = new() { Id = "dev-1", FriendlyName = "Speakers" };

    private sealed record Shown(ControlsPage Page, Window Window, TestRig Rig, SettingsViewModel Vm) : IDisposable
    {
        public ControlsViewModel Controls => Vm.Controls;
        public AppConfig Config => Rig.Config.Config;
        public ActionDialogViewModel Dialog => Assert.IsType<ActionDialogViewModel>(Controls.Dialog);
        public ActionDialog View => Page.GetVisualDescendants().OfType<ActionDialog>().Single();

        public void Dispose()
        {
            Vm.Dispose();
            Rig.Dispose();
        }
    }

    /// <param name="services">Media, program and script services; false: those actions are unavailable.</param>
    /// <param name="media">The media sessions, when a test needs their apps.</param>
    private static Shown Show(Action<AppConfig>? configure = null, bool services = true, FakeMediaSessions? media = null)
    {
        var speakers = new AudioDevice("dev-1", "Speakers", AudioFlow.Render, true);
        var engine = new FakeAudioEngine
        {
            Devices = { speakers, new AudioDevice("dev-2", "Headphones", AudioFlow.Render, false) },
            DefaultRender = speakers,
        };
        var custom = services ? new CustomActionServices(media ?? new FakeMediaSessions(), new FakeProgramLauncher(), new FakeProcessStarter()) : null;
        var rig = new TestRig(engine, custom: custom);
        configure?.Invoke(rig.Config.Config);
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new ControlsPage(vm.Controls);
        var window = new Window { Content = page, Width = 932, Height = 680 };
        window.Show();
        Settle(window);
        return new Shown(page, window, rig, vm);
    }

    /// <summary>Selects the button and opens its gesture's dialog from the inspector row, as a click would.</summary>
    private static void Open(Shown s, int button = 0, string row = "Press")
    {
        s.Controls.Select(s.Vm.Buttons.Items[button]);
        Settle(s.Window);
        var rowButton = RowButton(s, row);
        rowButton.Focus(NavigationMethod.Pointer);
        rowButton.Command!.Execute(rowButton.CommandParameter);
        Settle(s.Window);
    }

    private static Button RowButton(Shown s, string row)
        => In<Button>(In<ContentControl>(s.Page.FindControl<Border>("Inspector")!, $"ActionRow_{row}"), "RowButton");

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static T In<T>(Visual root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static List<Button> ItemButtons(Shown s)
        => s.View.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "ActionItemButton" && b.IsEffectivelyVisible).ToList();

    private static Button ItemButton(Shown s, string name)
        => ItemButtons(s).First(b => ((ActionItem)b.DataContext!).Name == name);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void PressKey(Window window, Key key, PhysicalKey physical)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        window.KeyRelease(key, RawInputModifiers.None, physical, null);
        Settle(window);
    }

    private static void TypeText(Shown s, string text)
    {
        s.Window.KeyTextInput(text);
        Settle(s.Window);
    }

    private static TextBox SearchBox(Shown s) => In<TextBox>(s.View, "SearchBox");

    private static Control? Focused(Window window) => window.FocusManager?.GetFocusedElement() as Control;

    // ── Opening and the list ──────────────────────────────────────────────────

    [AvaloniaFact]
    public void Open_SearchFocused_FullList()
    {
        using var s = Show();
        Open(s);

        Assert.True(SearchBox(s).IsFocused);
        Assert.Equal("Search actions", SearchBox(s).PlaceholderText);
        Assert.Equal("Press", In<TextBlock>(s.View, "TitleText").Text);
        Assert.Equal("Button 1 · Default", In<TextBlock>(s.View, "SubtitleText").Text);
        Assert.Equal(new[] { "Sound", "Media", "Modes", "Programs" }, s.Dialog.Sections.Select(x => x.Name));
        Assert.Equal(
            new[] { "Mute main output", "Mute microphone", "Mute a device", "Switch output", "Toggle outputs",
                    "Play / pause", "Next track", "Previous track", "Stop", "Next mode", "Go to mode",
                    "Open a program", "Run a script" },
            s.Dialog.VisibleItems.Select(i => i.Name));
        var buttons = ItemButtons(s);
        Assert.Equal(13, buttons.Count);
        Assert.All(buttons, b => Assert.True(b.IsEffectivelyEnabled));

        // Nothing picked yet: the pane asks for an action, and Done waits for one.
        Assert.True(In<TextBlock>(s.View, "EmptyPaneText").IsEffectivelyVisible);
        Assert.Equal("Choose an action.", In<TextBlock>(s.View, "EmptyPaneText").Text);
        Assert.False(In<Button>(s.View, "DoneButton").IsEffectivelyEnabled);
        Assert.False(In<Button>(s.View, "RemoveButton").IsVisible);
        Assert.Equal(new[] { "Remove", "Cancel", "Done" },
                     new[] { "RemoveButton", "CancelButton", "DoneButton" }.Select(n => In<Button>(s.View, n).Content as string));
    }

    [AvaloniaFact]
    public void Query_tgo_ToggleFirst()
    {
        using var s = Show();
        Open(s);
        TypeText(s, "tgo");

        Assert.Equal("tgo", s.Dialog.Query);
        Assert.Equal(ButtonActionType.ToggleOutputDevice, s.Dialog.VisibleItems[0].Type);
        var first = ItemButtons(s)[0];
        Assert.Equal("Toggle outputs", ((ActionItem)first.DataContext!).Name);
        Assert.Equal(new[] { 0, 2, 7 }, ((ActionItem)first.DataContext!).Highlight);
        Assert.Equal(new[] { "T", "g", "o" }, In<HighlightText>(first, "ItemName").HighlightedRuns);
        // Its section shows its match count.
        var section = s.View.GetVisualDescendants().OfType<Button>().First(b => b.Name == "SectionHeader");
        Assert.True(In<TextBlock>(section, "MatchCountText").IsVisible);
        Assert.Equal("1", In<TextBlock>(section, "MatchCountText").Text);

        TypeText(s, "zz");
        Assert.Empty(ItemButtons(s));
        Assert.True(In<TextBlock>(s.View, "NoMatchText").IsEffectivelyVisible);
        Assert.Equal("No actions match “tgozz”.", In<TextBlock>(s.View, "NoMatchText").Text);
    }

    [AvaloniaFact]
    public void Query_Cleared_RestoresListAndCollapsed()
    {
        using var s = Show();
        Open(s);
        s.Dialog.ToggleSectionCommand.Execute("Sound");
        Settle(s.Window);
        Assert.Equal(8, ItemButtons(s).Count);

        TypeText(s, "mute");
        var sound = s.Dialog.Sections.Single(x => x.Name == "Sound");
        Assert.False(sound.IsCollapsed);   // open while searching
        Assert.Equal(3, sound.MatchCount);
        Assert.Contains(ItemButtons(s), b => ((ActionItem)b.DataContext!).Name == "Mute a device");
        Assert.True(s.Controls.CollapsedSections["action:Sound"]);   // remembered state unchanged

        // A header click while searching changes nothing.
        s.Dialog.ToggleSectionCommand.Execute("Sound");
        Assert.True(s.Controls.CollapsedSections["action:Sound"]);

        SearchBox(s).Text = "";
        Settle(s.Window);
        Assert.True(s.Dialog.Sections.Single(x => x.Name == "Sound").IsCollapsed);
        Assert.Equal(4, s.Dialog.Sections.Count);
        Assert.Equal(8, ItemButtons(s).Count);
        Assert.False(In<TextBlock>(s.View, "NoMatchText").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Collapse_Media_HidesItems_SearchReopens()
    {
        using var s = Show();
        Open(s);
        var media = s.View.GetVisualDescendants().OfType<Button>()
            .First(b => b.Name == "SectionHeader" && ((ActionSection)b.DataContext!).Name == "Media");
        media.Command!.Execute(media.CommandParameter);
        Settle(s.Window);

        Assert.True(s.Controls.CollapsedSections["action:Media"]);
        Assert.Equal(9, ItemButtons(s).Count);
        Assert.DoesNotContain(ItemButtons(s), b => ((ActionItem)b.DataContext!).Name == "Next track");

        TypeText(s, "next");
        Assert.Contains(ItemButtons(s), b => ((ActionItem)b.DataContext!).Name == "Next track");

        // The collapsed state lasts for the app run: the next dialog starts with Media closed.
        s.Dialog.Cancel();
        Open(s, row: "Hold");
        Assert.True(s.Dialog.Sections.Single(x => x.Name == "Media").IsCollapsed);
        Assert.Equal(9, ItemButtons(s).Count);
    }

    // ── Picking ───────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Pick_ShowsSettings_DoneEnabled()
    {
        using var s = Show();
        Open(s);
        Click(ItemButton(s, "Mute a device"));
        Settle(s.Window);

        Assert.Equal(ButtonActionType.MuteSpecificDevice, s.Dialog.Draft.Config.Action);
        Assert.False(In<TextBlock>(s.View, "EmptyPaneText").IsEffectivelyVisible);
        Assert.Equal("Mute a device", In<TextBlock>(s.View, "PickedNameText").Text);
        var combo = In<ComboBox>(s.View, "DeviceCombo");
        Assert.True(combo.IsEffectivelyVisible);
        Assert.Null(combo.SelectedItem);   // nothing pre-selected
        Assert.Null(s.Dialog.Draft.Config.Device);
        Assert.False(In<StackPanel>(s.View, "CommandParams").IsEffectivelyVisible);
        Assert.True(In<Button>(s.View, "DoneButton").IsEffectivelyEnabled);
        Assert.Contains("picked", ItemButton(s, "Mute a device").Classes);
        Assert.True(SearchBox(s).IsFocused);

        // A different action starts empty again.
        s.Dialog.Draft.Device = "Speakers";
        s.Dialog.Pick(ButtonActionType.SetOutputDevice);
        Settle(s.Window);
        Assert.Null(s.Dialog.Draft.Config.Device);
        Assert.Null(In<ComboBox>(s.View, "DeviceCombo").SelectedItem);
        Assert.Equal(ButtonActionType.None, s.Config.Buttons[0].Default().SinglePress.Action);   // nothing written yet
    }

    [AvaloniaFact]
    public void Enter_PicksTop()
    {
        using var s = Show();
        Open(s);
        TypeText(s, "tgo");
        PressKey(s.Window, Key.Enter, PhysicalKey.Enter);
        Assert.Equal(ButtonActionType.ToggleOutputDevice, s.Dialog.Draft.Config.Action);
        Assert.Null(s.Dialog.Draft.Config.Device);
        Assert.Null(s.Dialog.Draft.Config.DeviceB);
        Assert.True(s.Controls.HasDialog);

        // Down moves from the top result; Enter picks where it is.
        SearchBox(s).Text = "";
        TypeText(s, "mute");
        Assert.Equal(new[] { "Mute a device", "Mute microphone", "Mute main output" },
                     s.Dialog.VisibleItems.Take(3).Select(i => i.Name));
        PressKey(s.Window, Key.Down, PhysicalKey.ArrowDown);
        Assert.Contains("current", ItemButton(s, "Mute microphone").Classes);
        PressKey(s.Window, Key.Down, PhysicalKey.ArrowDown);
        PressKey(s.Window, Key.Up, PhysicalKey.ArrowUp);
        PressKey(s.Window, Key.Enter, PhysicalKey.Enter);
        Assert.Equal(ButtonActionType.MuteCurrentInput, s.Dialog.Draft.Config.Action);
    }

    [AvaloniaFact]
    public void Escape_Cancels_NoWrite()
    {
        using var s = Show(c => c.Buttons[0].Default().SinglePress = new ButtonActionConfig
        {
            Action = ButtonActionType.MuteSpecificDevice, Device = Speakers,
        });
        var saved = s.Config.Buttons[0].Default().SinglePress;
        Open(s);
        Assert.Equal(ButtonActionType.MuteSpecificDevice, s.Dialog.Draft.Config.Action);
        Assert.Equal("Speakers", s.Dialog.Draft.Device);
        s.Dialog.Pick(ButtonActionType.ToggleOutputDevice);
        s.Dialog.Draft.Device = "Headphones";

        PressKey(s.Window, Key.Escape, PhysicalKey.Escape);
        Assert.False(s.Controls.HasDialog);
        Assert.Same(saved, s.Config.Buttons[0].Default().SinglePress);
        Assert.Equal(ButtonActionType.MuteSpecificDevice, saved.Action);
        Assert.Equal("dev-1", saved.Device!.Id);

        // Cancel and a click on the scrim write nothing either.
        Open(s);
        s.Dialog.Pick(ButtonActionType.MuteCurrentInput);
        In<Button>(s.View, "CancelButton").Command!.Execute(null);
        Assert.False(s.Controls.HasDialog);
        Open(s);
        s.Dialog.Pick(ButtonActionType.MuteCurrentInput);
        var overlay = s.Page.FindControl<Border>("DialogOverlay")!;
        s.Window.MouseDown(overlay.TranslatePoint(new Point(4, 4), s.Window)!.Value, MouseButton.Left);
        s.Window.MouseUp(overlay.TranslatePoint(new Point(4, 4), s.Window)!.Value, MouseButton.Left);
        Settle(s.Window);
        Assert.False(s.Controls.HasDialog);
        Assert.Same(saved, s.Config.Buttons[0].Default().SinglePress);
        Assert.Equal(ButtonActionType.MuteSpecificDevice, saved.Action);
    }

    [AvaloniaFact]
    public void Escape_WithDropdownOpen_ClosesOnlyTheDropdown()
    {
        using var s = Show();
        Open(s);
        s.Dialog.Pick(ButtonActionType.SetOutputDevice);
        Settle(s.Window);
        s.Dialog.Draft.Device = "Headphones";
        var combo = In<ComboBox>(s.View, "DeviceCombo");
        combo.Focus(NavigationMethod.Pointer);
        combo.IsDropDownOpen = true;
        Settle(s.Window);

        PressKey(s.Window, Key.Escape, PhysicalKey.Escape);
        Assert.False(combo.IsDropDownOpen);
        Assert.True(s.Controls.HasDialog);
        Assert.Equal("Headphones", s.Dialog.Draft.Device);   // the draft is kept

        PressKey(s.Window, Key.Escape, PhysicalKey.Escape);
        Assert.False(s.Controls.HasDialog);
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Done_RunScriptNoPath_ShowsErrorFocusesField()
    {
        using var s = Show();
        Open(s);
        s.Dialog.Pick(ButtonActionType.RunScript);
        Settle(s.Window);
        Assert.False(In<TextBlock>(s.View, "PathErrorText").IsVisible);   // no errors before Done

        Click(In<Button>(s.View, "DoneButton"));
        Settle(s.Window);

        Assert.True(s.Controls.HasDialog);
        var error = In<TextBlock>(s.View, "PathErrorText");
        Assert.True(error.IsEffectivelyVisible);
        Assert.Equal("Choose the script to run.", error.Text);
        var box = In<TextBox>(s.View, "CommandPathBox");
        Assert.Contains("Invalid", box.Classes);
        Assert.True(box.IsFocused);
        Assert.Equal(ButtonActionType.None, s.Config.Buttons[0].Default().SinglePress.Action);
    }

    [AvaloniaFact]
    public void Errors_LiveAfterFirstDone()
    {
        using var s = Show();
        Open(s);
        s.Dialog.Pick(ButtonActionType.RunScript);
        Settle(s.Window);
        Click(In<Button>(s.View, "DoneButton"));
        Settle(s.Window);

        TypeText(s, "lights.bat");   // into the focused file box
        Assert.Equal("lights.bat", s.Dialog.Draft.CommandPath);
        Assert.Equal("Enter the full path, starting with a drive letter, e.g. C:\\…", In<TextBlock>(s.View, "PathErrorText").Text);

        s.Dialog.Draft.CommandPath = @"C:\Scripts\lights.txt";
        Settle(s.Window);
        Assert.Equal("Volco runs .bat, .cmd, .ps1, .py, .js and .exe files.", In<TextBlock>(s.View, "PathErrorText").Text);

        s.Dialog.Draft.CommandWorkingDirectory = "scripts";
        Settle(s.Window);
        Assert.Equal("Enter the folder’s full path, or leave it empty.", In<TextBlock>(s.View, "WorkingDirectoryErrorText").Text);
        s.Dialog.Draft.CommandWorkingDirectory = "";
        Settle(s.Window);
        Assert.False(In<TextBlock>(s.View, "WorkingDirectoryErrorText").IsVisible);
        Assert.DoesNotContain("Invalid", In<TextBox>(s.View, "CommandFolderBox").Classes);
    }

    [AvaloniaFact]
    public void PickOtherType_ResetsErrors()
    {
        using var s = Show();
        Open(s);
        s.Dialog.Pick(ButtonActionType.RunScript);
        Assert.False(s.Dialog.Done());
        Assert.NotEmpty(s.Dialog.Draft.Errors);

        s.Dialog.Pick(ButtonActionType.OpenProgram);
        Settle(s.Window);
        Assert.Empty(s.Dialog.Draft.Errors);
        Assert.False(s.Dialog.Draft.ShowErrors);
        Assert.False(In<TextBlock>(s.View, "PathErrorText").IsVisible);

        // Editing after the reset doesn't bring errors back until the next Done.
        s.Dialog.Draft.CommandPath = "game.exe";
        Assert.Empty(s.Dialog.Draft.Errors);

        s.Dialog.Pick(ButtonActionType.ToggleOutputDevice);
        Assert.False(s.Dialog.Done());
        Assert.Equal("Choose a device.", s.Dialog.Draft.DeviceError);
        Assert.Equal("Choose a device.", s.Dialog.Draft.DeviceBError);
        s.Dialog.Draft.Device = "Speakers";
        s.Dialog.Draft.DeviceB = "Speakers";
        Assert.Null(s.Dialog.Draft.DeviceError);
        Assert.Equal("Choose a different device from A.", s.Dialog.Draft.DeviceBError);
    }

    [AvaloniaFact]
    public void Done_Valid_WritesEditedPresetSlot()
    {
        using var s = Show(c => c.Presets.Add(new PresetConfig { Id = "preset-2", Name = "Gaming" }));
        s.Vm.Buttons.EditingPresetId = "preset-2";
        Settle(s.Window);
        Open(s, button: 1, row: "Hold");
        Assert.Equal("Button 2 · Gaming", s.Dialog.Subtitle);
        Assert.Equal("Hold", s.Dialog.Title);

        s.Dialog.Pick(ButtonActionType.GoToPreset);
        Settle(s.Window);
        Click(In<Button>(s.View, "DoneButton"));
        Settle(s.Window);
        Assert.True(s.Controls.HasDialog);
        Assert.Equal("Choose a mode.", In<TextBlock>(s.View, "PresetErrorText").Text);
        Assert.True(In<ComboBox>(s.View, "PresetCombo").IsFocused);
        s.Dialog.Draft.SelectedPreset = s.Dialog.Draft.PresetChoices.Single(p => p.Id == AppConfig.DefaultPresetId);
        Click(In<Button>(s.View, "DoneButton"));
        Settle(s.Window);

        Assert.False(s.Controls.HasDialog);
        var written = s.Config.Buttons[1].Actions["preset-2"].LongPress;
        Assert.Equal(ButtonActionType.GoToPreset, written.Action);
        Assert.Equal(AppConfig.DefaultPresetId, written.PresetId);
        Assert.Equal(ButtonActionType.None, s.Config.Buttons[1].BindingFor(AppConfig.DefaultPresetId).LongPress.Action);
        // The row, the tile and the problems follow, as after any edit.
        var item = s.Vm.Buttons.Items[1];
        Assert.Same(written, item.Hold.Config);
        Assert.Equal("Go to Default", In<TextBlock>(In<ContentControl>(s.Page.FindControl<Border>("Inspector")!, "ActionRow_Hold"), "ActionText").Text);
        Assert.Equal("Go to Default", item.MainLabel);
        Assert.Null(item.Hold.Problem);
    }

    [AvaloniaFact]
    public void Done_UnchangedInvalid_ShowsErrors_StaysOpen()
    {
        // A saved action with a problem: Done validates even when nothing changed.
        using var s = Show(c => c.Buttons[0].Default().SinglePress = new ButtonActionConfig { Action = ButtonActionType.MuteSpecificDevice });
        var saved = s.Config.Buttons[0].Default().SinglePress;
        Open(s);
        Click(In<Button>(s.View, "DoneButton"));
        Settle(s.Window);

        Assert.True(s.Controls.HasDialog);
        Assert.Equal("Choose a device.", s.Dialog.Draft.DeviceError);
        Assert.True(In<TextBlock>(s.View, "DeviceErrorText").IsEffectivelyVisible);
        Assert.Same(saved, s.Config.Buttons[0].Default().SinglePress);
    }

    [AvaloniaFact]
    public void Done_UnchangedValid_ClosesWithoutWriting()
    {
        using var s = Show(c => c.Buttons[0].Default().SinglePress = new ButtonActionConfig { Action = ButtonActionType.MuteCurrentOutput });
        var saved = s.Config.Buttons[0].Default().SinglePress;
        Open(s);
        Assert.True(s.Dialog.Done());
        Assert.False(s.Controls.HasDialog);
        Assert.Same(saved, s.Config.Buttons[0].Default().SinglePress);
    }

    [AvaloniaFact]
    public void QuotedPaths_AreSavedUnquoted()
    {
        var script = $@"C:\volco-test-{Guid.NewGuid()}.bat";   // a Windows full path; on Linux a file by that name in the working directory
        File.WriteAllText(script, "");
        try
        {
            using var s = Show();
            Open(s);
            s.Dialog.Pick(ButtonActionType.RunScript);
            s.Dialog.Draft.CommandPath = $"\"{script}\"";   // Explorer's "Copy as path"
            s.Dialog.Draft.CommandWorkingDirectory = "\"C:\\Scripts\"";
            Assert.True(s.Dialog.Done());

            var saved = s.Config.Buttons[0].Default().SinglePress.Command!;
            Assert.Equal(script, saved.Path);
            Assert.Equal(@"C:\Scripts", saved.WorkingDirectory);
        }
        finally { File.Delete(script); }
    }

    [AvaloniaFact]
    public void Remove_ClearsSlot()
    {
        using var s = Show(c => c.Buttons[0].Default().DoublePress = new ButtonActionConfig { Action = ButtonActionType.MuteCurrentOutput });
        Open(s, row: "Double");
        Assert.True(s.Dialog.CanRemove);
        var remove = In<Button>(s.View, "RemoveButton");
        Assert.True(remove.IsEffectivelyVisible);
        Assert.Equal(ButtonActionType.MuteCurrentOutput, s.Dialog.Draft.Config.Action);
        Assert.True(In<Button>(s.View, "DoneButton").IsEffectivelyEnabled);

        remove.Command!.Execute(null);
        Settle(s.Window);

        Assert.False(s.Controls.HasDialog);
        Assert.Equal(ButtonActionType.None, s.Config.Buttons[0].Default().DoublePress.Action);
        Assert.Equal("—", s.Vm.Buttons.Items[0].DoublePress.RowText);
    }

    [AvaloniaFact]
    public void UnavailableAction_Disabled()
    {
        using var s = Show(services: false);
        Open(s);

        var disabled = s.Dialog.VisibleItems.Where(i => !i.IsEnabled).Select(i => i.Type).ToList();
        Assert.Equal(new[]
        {
            ButtonActionType.MediaPlayPause, ButtonActionType.MediaNext, ButtonActionType.MediaPrevious,
            ButtonActionType.MediaStop, ButtonActionType.OpenProgram, ButtonActionType.RunScript,
        }, disabled);
        Assert.False(ItemButton(s, "Run a script").IsEffectivelyEnabled);
        Assert.True(ItemButton(s, "Next mode").IsEffectivelyEnabled);

        s.Dialog.Pick(ButtonActionType.RunScript);
        Assert.Equal(ButtonActionType.None, s.Dialog.Draft.Config.Action);
        // Enter skips them too.
        TypeText(s, "run");
        PressKey(s.Window, Key.Enter, PhysicalKey.Enter);
        Assert.NotEqual(ButtonActionType.RunScript, s.Dialog.Draft.Config.Action);
    }

    [AvaloniaFact]
    public void EditedPresetDeleted_WhileOpen_ClosesWithoutWrite()
    {
        using var s = Show(c => c.Presets.Add(new PresetConfig { Id = "preset-2", Name = "Gaming" }));
        s.Vm.Buttons.EditingPresetId = "preset-2";
        Settle(s.Window);
        Open(s);
        s.Dialog.Pick(ButtonActionType.MuteCurrentInput);
        var dialog = s.Dialog;

        s.Vm.Buttons.DeleteMode("preset-2");
        Settle(s.Window);

        Assert.False(s.Controls.HasDialog);
        Assert.False(dialog.Done());   // a late Done writes nothing either
        Assert.All(s.Config.Buttons, b => Assert.False(b.Actions.ContainsKey("preset-2")));
        Assert.Equal(ButtonActionType.None, s.Config.Buttons[0].BindingFor(AppConfig.DefaultPresetId).SinglePress.Action);
    }

    // ── Each action's settings ────────────────────────────────────────────────

    [AvaloniaFact]
    public void SavedDevices_AreSelected_ByNameToo_AndShowNicknames()
    {
        using var s = Show(c =>
        {
            c.Devices.Add(new DeviceSettingsConfig { Id = "dev-2", Nickname = "Cans" });
            // Saved by friendly name only: one plugged in, one not.
            c.Buttons[0].Default().SinglePress = new ButtonActionConfig
            {
                Action = ButtonActionType.ToggleOutputDevice,
                Device = new DeviceRefConfig { Id = "", FriendlyName = "Speakers" },
                DeviceB = new DeviceRefConfig { Id = "", FriendlyName = "Old Soundcard" },
            };
            c.Buttons[1].Default().SinglePress = new ButtonActionConfig
            {
                Action = ButtonActionType.MuteSpecificDevice, Device = new DeviceRefConfig { Id = "dev-2", FriendlyName = "Headphones" },
            };
        });
        Open(s);
        var a = In<ComboBox>(s.View, "DeviceACombo");
        var b = In<ComboBox>(s.View, "DeviceBCombo");
        Assert.Equal("Speakers", a.SelectedItem);
        Assert.Equal("Old Soundcard", b.SelectedItem);
        Assert.Equal(new[] { "Speakers", "Headphones", "Old Soundcard" }, Assert.IsAssignableFrom<IEnumerable<string>>(a.ItemsSource));
        s.Dialog.Cancel();

        // The picker shows the nickname, with the Windows name as secondary text.
        Open(s, button: 1);
        var combo = In<ComboBox>(s.View, "DeviceCombo");
        Assert.Equal("Headphones", combo.SelectedItem);
        Assert.Equal("Cans", In<TextBlock>(combo, "DeviceLabel").Text);
        Assert.Equal("Headphones", In<TextBlock>(combo, "DeviceSecondary").Text);
        combo.IsDropDownOpen = true;
        Settle(s.Window);
        var labels = Enumerable.Range(0, combo.ItemCount)
            .Select(i => In<TextBlock>(Assert.IsAssignableFrom<Control>(combo.ContainerFromIndex(i)), "DeviceLabel").Text)
            .ToList();
        Assert.Equal(new[] { "Speakers", "Cans" }, labels);
    }

    [AvaloniaFact]
    public void RunScript_ShowsTheCommandFields_AndTheUsbNote()
    {
        var script = $@"C:\volco-test-{Guid.NewGuid()}.bat";   // a Windows full path; on Linux a file by that name in the working directory
        File.WriteAllText(script, "");
        try
        {
            using var s = Show();
            Open(s);
            Click(ItemButton(s, "Run a script"));
            Settle(s.Window);

            Assert.True(In<StackPanel>(s.View, "CommandParams").IsEffectivelyVisible);
            Assert.True(In<CheckBox>(s.View, "StartAnotherCheck").IsEffectivelyVisible);
            Assert.True(In<StackPanel>(s.View, "UsbNote").IsEffectivelyVisible);
            Assert.False(In<StackPanel>(s.View, "MediaParams").IsEffectivelyVisible);
            Assert.False(In<StackPanel>(s.View, "DeviceParams").IsEffectivelyVisible);

            s.Dialog.Draft.CommandPath = script;
            s.Dialog.Draft.CommandArguments = "on";
            s.Dialog.Draft.CommandName = "Lights";
            s.Dialog.Draft.StartAnother = true;
            Click(In<Button>(s.View, "DoneButton"));
            Settle(s.Window);

            Assert.False(s.Controls.HasDialog);
            var saved = s.Config.Buttons[0].Default().SinglePress;
            Assert.Equal(ButtonActionType.RunScript, saved.Action);
            Assert.Equal(script, saved.Command!.Path);
            Assert.Equal("on", saved.Command.Arguments);
            Assert.Equal("Lights", saved.Command.Name);
            Assert.Equal(WhileRunningPolicy.StartAnother, saved.WhileRunning);
            Assert.Equal("Run Lights", s.Vm.Buttons.Items[0].MainLabel);
        }
        finally { File.Delete(script); }
    }

    [AvaloniaFact]
    public void OpenProgram_HasNoRepeatChoice_RefusesAMissingFile_AcceptsALink()
    {
        using var s = Show();
        Open(s);
        Click(ItemButton(s, "Open a program"));
        Settle(s.Window);
        Assert.False(In<CheckBox>(s.View, "StartAnotherCheck").IsVisible);
        Assert.True(In<StackPanel>(s.View, "UsbNote").IsEffectivelyVisible);

        s.Dialog.Draft.CommandPath = $@"C:\Games\missing-{Guid.NewGuid()}.exe";
        Click(In<Button>(s.View, "DoneButton"));
        Settle(s.Window);
        Assert.True(s.Controls.HasDialog);
        Assert.Equal("This file doesn’t exist.", In<TextBlock>(s.View, "PathErrorText").Text);

        s.Dialog.Draft.CommandPath = "steam://rungameid/570";
        Settle(s.Window);
        Assert.False(In<TextBlock>(s.View, "PathErrorText").IsVisible);
        Click(In<Button>(s.View, "DoneButton"));
        Settle(s.Window);
        Assert.False(s.Controls.HasDialog);
        Assert.Equal("steam://rungameid/570", s.Config.Buttons[0].Default().SinglePress.Command!.Path);
    }

    [AvaloniaFact]
    public void MediaPicker_ListsCurrent_LiveApps_AndTheSavedMissingOne()
    {
        var media = new FakeMediaSessions();
        media.SetApps(new[] { new MediaApp("chrome", "Google Chrome", MediaPlayback.Playing) });
        using var s = Show(c => c.Buttons[0].Default().SinglePress = new ButtonActionConfig
        {
            Action = ButtonActionType.MediaPlayPause, MediaApp = new MediaAppConfig { Id = "Spotify.exe", Name = "Spotify" },
        }, media: media);
        Open(s);
        var draft = s.Dialog.Draft;

        Assert.True(In<StackPanel>(s.View, "MediaParams").IsEffectivelyVisible);
        Assert.Equal(new[] { "Current app (whatever plays)", "Google Chrome", "Spotify (not running)" },
                     draft.MediaAppChoices.Select(c => c.Label));
        Assert.Equal("Spotify.exe", draft.SelectedMediaApp!.Id);
        Assert.Same(draft.SelectedMediaApp, In<ComboBox>(s.View, "MediaAppCombo").SelectedItem);

        draft.SelectedMediaApp = draft.MediaAppChoices[1];
        Click(In<Button>(s.View, "DoneButton"));
        Settle(s.Window);
        var saved = s.Config.Buttons[0].Default().SinglePress;
        Assert.Equal("chrome", saved.MediaApp!.Id);
        Assert.Equal("Google Chrome", saved.MediaApp.Name);
        Assert.Equal("Play/pause Google Chrome", s.Vm.Buttons.Items[0].MainLabel);

        Open(s);
        s.Dialog.Draft.SelectedMediaApp = s.Dialog.Draft.MediaAppChoices[0];
        Click(In<Button>(s.View, "DoneButton"));
        Settle(s.Window);
        Assert.Null(s.Config.Buttons[0].Default().SinglePress.MediaApp);
    }

    // ── Focus and live devices ────────────────────────────────────────────────

    [AvaloniaFact]
    public void Focus_TabStaysInDialog_ReturnsToRowOnClose()
    {
        using var s = Show();
        Open(s, row: "Double");
        Assert.True(SearchBox(s).IsFocused);
        s.Dialog.Pick(ButtonActionType.RunScript);   // more fields to tab through
        Settle(s.Window);

        var card = s.Page.FindControl<Border>("DialogCard")!;
        for (int i = 0; i < 30; i++)
        {
            PressKey(s.Window, Key.Tab, PhysicalKey.Tab);
            var focused = Focused(s.Window);
            Assert.NotNull(focused);
            Assert.True(card.IsVisualAncestorOf(focused!), $"Tab {i + 1} left the dialog for {focused!.Name ?? focused.GetType().Name}");
        }

        PressKey(s.Window, Key.Escape, PhysicalKey.Escape);
        Assert.False(s.Controls.HasDialog);
        Assert.Same(RowButton(s, "Double"), Focused(s.Window));
    }

    [AvaloniaFact]
    public void DevicePluggedIn_WhileOpen_AppearsInPicker()
    {
        using var s = Show();
        Open(s);
        s.Dialog.Pick(ButtonActionType.SetOutputDevice);
        Settle(s.Window);

        var added = new AudioDevice("dev-3", "USB DAC", AudioFlow.Render, false);
        s.Rig.Engine.Devices.Add(added);
        s.Rig.Engine.RaiseDeviceChanged(DeviceChangeKind.Added, added);
        Settle(s.Window);

        Assert.Contains("USB DAC", s.Dialog.Draft.DeviceNames);
        Assert.Contains("USB DAC", Assert.IsAssignableFrom<IEnumerable<string>>(In<ComboBox>(s.View, "DeviceCombo").ItemsSource));

        // A closed dialog stops following.
        var closed = s.Dialog;
        closed.Cancel();
        var gone = new AudioDevice("dev-4", "Dock", AudioFlow.Render, false);
        s.Rig.Engine.Devices.Add(gone);
        s.Rig.Engine.RaiseDeviceChanged(DeviceChangeKind.Added, gone);
        Settle(s.Window);
        Assert.DoesNotContain("Dock", closed.Draft.DeviceNames);
    }
}
