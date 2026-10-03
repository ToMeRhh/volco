using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
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

namespace Volco.App.Tests.Pages;

public class ButtonsPageTests
{
    private static FakeAudioEngine EngineWithAudio()
    {
        var speakers = new AudioDevice("dev-1", "Speakers", AudioFlow.Render, true);
        var engine = new FakeAudioEngine
        {
            Devices = { speakers, new AudioDevice("dev-2", "Headphones", AudioFlow.Render, false) },
            DefaultRender = speakers,
        };
        engine.DeviceStates["dev-1"] = (0.5f, false);
        return engine;
    }

    private static (ButtonsPage Page, TestRig Rig, SettingsViewModel Vm) Show(Action<AppConfig>? configure = null)
    {
        var rig = new TestRig(EngineWithAudio());
        configure?.Invoke(rig.Config.Config);
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new ButtonsPage(vm.Buttons);
        new Window { Content = page, Width = 900, Height = 900 }.Show();
        Dispatcher.UIThread.RunJobs();
        return (page, rig, vm);
    }

    private static List<T> All<T>(Control root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().Where(c => c.Name == name).ToList();

    private static Border Card(ButtonsPage page, int index) => All<Border>(page, "Card")[index];

    private static T In<T>(Control root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    /// <summary>A control inside one action row ("PressRow", "DoubleRow", "HoldRow") of a card.</summary>
    private static T InRow<T>(ButtonsPage page, string row, string name, int button = 0) where T : Control
        => In<T>(In<ActionRow>(Card(page, button), row), name);

    private static void Expand(SettingsViewModel vm, int index)
    {
        vm.Buttons.Items[index].IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
    }

    private static ButtonBindingConfig DefaultOf(AppConfig c, int button) => c.Buttons[button].Default();

    // ── Rendering ─────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void ButtonsPage_RendersOneCardPerButton_WithThreeActionRows()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        int buttons = rig.Config.Config.Buttons.Count;
        Expand(vm, 0);

        Assert.Equal(buttons, All<Border>(page, "Card").Count);
        Assert.Equal(new[] { "Button 0", "Button 1", "Button 2" },
            All<TextBlock>(page, "TitleText").Select(t => t.Text).ToArray());
        Assert.Equal(new[] { "Press", "Double press", "Hold" },
            new[] { "PressRow", "DoubleRow", "HoldRow" }
                .Select(r => In<TextBlock>(In<ActionRow>(Card(page, 0), r), "LabelText").Text).ToArray());
        Assert.All(All<TextBlock>(page, "SummaryText"), t => Assert.Equal("Not set up", t.Text));
    }

    [AvaloniaFact]
    public void ButtonsPage_Summary_ListsTheEditedPresetsActions()
    {
        var (page, rig, vm) = Show(c =>
        {
            DefaultOf(c, 0).SinglePress.Action = ButtonActionType.MuteCurrentInput;
            DefaultOf(c, 0).LongPress.Action = ButtonActionType.NextPreset;
            DefaultOf(c, 1).SinglePress = new ButtonActionConfig
            {
                Action = ButtonActionType.ToggleOutputDevice,
                Device = new DeviceRefConfig { Id = "dev-1", FriendlyName = "Speakers (Realtek)" },
                DeviceB = new DeviceRefConfig { Id = "dev-2", FriendlyName = "Headphones" },
            };
        });
        using var _ = rig; using var __ = vm;

        Assert.Equal(new[] { "Mute mic · Hold: Next preset", "Toggle Speakers / Headphones", "Not set up" },
            All<TextBlock>(page, "SummaryText").Select(t => t.Text).ToArray());
    }

    // ── Action rows ───────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void ButtonsPage_ActionPicker_IsGrouped_AndHeadersCannotBeChosen()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        Expand(vm, 0);
        var picker = InRow<ComboBox>(page, "PressRow", "ActionCombo");

        var items = Assert.IsAssignableFrom<IEnumerable<ActionChoice>>(picker.ItemsSource).ToList();
        Assert.Equal(new[] { "Audio", "Presets", "Custom" }, items.Where(i => i.IsHeader).Select(i => i.Name));
        Assert.False(items.Single(i => i.Name.StartsWith("Run a program")).IsSelectable);

        picker.SelectedItem = items.First(i => i.IsHeader);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(ButtonActionType.None, DefaultOf(rig.Config.Config, 0).SinglePress.Action);
        Assert.Equal("Do nothing", vm.Buttons.Items[0].Press.SelectedChoice.Name);
    }

    [AvaloniaFact]
    public void ButtonsPage_ActionNeedingDevice_ShowsDevicePicker()
    {
        var (page, rig, vm) = Show(c => DefaultOf(c, 0).SinglePress.Action = ButtonActionType.MuteSpecificDevice);
        using var _ = rig; using var __ = vm;
        Expand(vm, 0);

        Assert.True(InRow<Border>(page, "PressRow", "DeviceParams").IsVisible);
        Assert.False(InRow<Border>(page, "PressRow", "PairParams").IsVisible);
        Assert.False(InRow<Border>(page, "DoubleRow", "DeviceParams").IsVisible);
        Assert.False(InRow<Border>(page, "HoldRow", "DeviceParams").IsVisible);
    }

    [AvaloniaFact]
    public void ButtonsPage_FriendlyNameOnlyDevice_IsSelected()
    {
        var (page, rig, vm) = Show(c =>
        {
            DefaultOf(c, 0).SinglePress.Action = ButtonActionType.SetOutputDevice;
            DefaultOf(c, 0).SinglePress.Device = new DeviceRefConfig { Id = "", FriendlyName = "Speakers" };
        });
        using var _ = rig; using var __ = vm;
        Expand(vm, 0);
        var picker = InRow<ComboBox>(page, "PressRow", "DeviceCombo");

        Assert.True(picker.IsVisible);
        Assert.Equal("Speakers", picker.SelectedItem);
        Assert.True(picker.SelectedIndex >= 0);
        Assert.Contains("Speakers", Assert.IsAssignableFrom<IEnumerable<string>>(picker.ItemsSource));
    }

    [AvaloniaFact]
    public void ButtonsPage_ChoosingActionAndDevice_PersistsIdAndNameToConfig()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        Expand(vm, 0);

        vm.Buttons.Items[0].Press.SelectedChoice = ActionCatalog.For(ButtonActionType.SetOutputDevice);
        Dispatcher.UIThread.RunJobs();
        InRow<ComboBox>(page, "PressRow", "DeviceCombo").SelectedItem = "Headphones";

        var cfg = DefaultOf(rig.Config.Config, 0).SinglePress;
        Assert.Equal(ButtonActionType.SetOutputDevice, cfg.Action);
        Assert.Equal("dev-2", cfg.Device!.Id);
        Assert.Equal("Headphones", cfg.Device.FriendlyName);
    }

    // Ported from the Output Devices page: the toggle pair now lives on the action.

    [AvaloniaFact]
    public void ButtonsPage_TogglePair_PickersListRenderDevices_AndWriteAAndB()
    {
        var (page, rig, vm) = Show(c => DefaultOf(c, 2).SinglePress.Action = ButtonActionType.ToggleOutputDevice);
        using var _ = rig; using var __ = vm;
        Expand(vm, 2);

        Assert.True(InRow<Border>(page, "PressRow", "PairParams", 2).IsVisible);
        foreach (var name in new[] { "DeviceACombo", "DeviceBCombo" })
            Assert.Equal(new[] { "Speakers", "Headphones" },
                Assert.IsAssignableFrom<IEnumerable<string>>(InRow<ComboBox>(page, "PressRow", name, 2).ItemsSource));

        // B sits under A (side by side, B overflowed the card).
        var comboA = InRow<ComboBox>(page, "PressRow", "DeviceACombo", 2);
        var comboB = InRow<ComboBox>(page, "PressRow", "DeviceBCombo", 2);
        var offset = comboB.TranslatePoint(new Point(0, 0), comboA)!.Value;
        Assert.Equal(0, offset.X);
        Assert.True(offset.Y >= comboA.Bounds.Height);

        InRow<ComboBox>(page, "PressRow", "DeviceACombo", 2).SelectedItem = "Headphones";
        InRow<ComboBox>(page, "PressRow", "DeviceBCombo", 2).SelectedItem = "Speakers";

        var toggle = DefaultOf(rig.Config.Config, 2).SinglePress;
        Assert.Equal("dev-2", toggle.Device!.Id);
        Assert.Equal("Headphones", toggle.Device.FriendlyName);
        Assert.Equal("dev-1", toggle.DeviceB!.Id);
    }

    [AvaloniaFact]
    public void ButtonsPage_TogglePair_FriendlyNameOnlyDevicesAreSelected()
    {
        var (page, rig, vm) = Show(c => DefaultOf(c, 2).SinglePress = new ButtonActionConfig
        {
            Action = ButtonActionType.ToggleOutputDevice,
            Device = new DeviceRefConfig { Id = "", FriendlyName = "Speakers" },
            DeviceB = new DeviceRefConfig { Id = "", FriendlyName = "Old Soundcard" },
        });
        using var _ = rig; using var __ = vm;
        Expand(vm, 2);

        Assert.Equal("Speakers", InRow<ComboBox>(page, "PressRow", "DeviceACombo", 2).SelectedItem);
        Assert.Equal("Old Soundcard", InRow<ComboBox>(page, "PressRow", "DeviceBCombo", 2).SelectedItem);
    }

    [AvaloniaFact]
    public void ButtonsPage_TogglePair_DeviceAddedEvent_AppearsInPickers()
    {
        var (page, rig, vm) = Show(c => DefaultOf(c, 2).SinglePress.Action = ButtonActionType.ToggleOutputDevice);
        using var _ = rig; using var __ = vm;
        Expand(vm, 2);

        var added = new AudioDevice("dev-3", "USB DAC", AudioFlow.Render, false);
        rig.Engine.Devices.Add(added);
        rig.Engine.RaiseDeviceChanged(DeviceChangeKind.Added, added);
        Dispatcher.UIThread.RunJobs();

        foreach (var name in new[] { "DeviceACombo", "DeviceBCombo" })
            Assert.Contains("USB DAC",
                Assert.IsAssignableFrom<IEnumerable<string>>(InRow<ComboBox>(page, "PressRow", name, 2).ItemsSource));
    }

    [AvaloniaFact]
    public void ButtonsPage_GoToPreset_OffersThePresets()
    {
        var (page, rig, vm) = Show(c =>
        {
            c.Presets.Add(new PresetConfig { Id = "gaming", Name = "Gaming" });
            DefaultOf(c, 0).LongPress.Action = ButtonActionType.GoToPreset;
        });
        using var _ = rig; using var __ = vm;
        Expand(vm, 0);
        var picker = InRow<ComboBox>(page, "HoldRow", "PresetCombo");

        Assert.True(InRow<Border>(page, "HoldRow", "PresetParams").IsVisible);
        Assert.Equal(new[] { "Default", "Gaming" },
            Assert.IsAssignableFrom<IEnumerable<PresetChoice>>(picker.ItemsSource).Select(p => p.Name));

        picker.SelectedItem = new PresetChoice("gaming", "Gaming");
        Assert.Equal("gaming", DefaultOf(rig.Config.Config, 0).LongPress.PresetId);
    }

    [AvaloniaFact]
    public void ButtonsPage_Test_RunsTheActionNow()
    {
        var (page, rig, vm) = Show(c => DefaultOf(c, 0).SinglePress.Action = ButtonActionType.MuteCurrentOutput);
        using var _ = rig; using var __ = vm;
        Expand(vm, 0);

        var test = InRow<Button>(page, "PressRow", "TestButton");
        Assert.True(test.IsEnabled);
        test.Command!.Execute(null);

        Assert.Single(rig.Engine.ToggleMuteCalls);
        Assert.False(InRow<Button>(page, "DoubleRow", "TestButton").IsEnabled);   // nothing to test
    }

    [AvaloniaFact]
    public void ButtonsPage_Timing_IsUnderAdvanced_AndWritesThrough()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        Expand(vm, 0);
        Assert.False(In<Border>(Card(page, 0), "AdvancedSection").IsVisible);

        In<ToggleButton>(Card(page, 0), "AdvancedToggle").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        In<NumericUpDown>(Card(page, 0), "LongPressBox").Value = 800;
        In<NumericUpDown>(Card(page, 0), "DoublePressBox").Value = 250;

        Assert.Equal(800, rig.Config.Config.Buttons[0].LongPressMs);
        Assert.Equal(250, rig.Config.Config.Buttons[0].DoublePressWindowMs);
    }

    // ── Controller light preview ──────────────────────────────────────────────

    [AvaloniaFact]
    public void ButtonsPage_LightPreview_FollowsTheLiveState()
    {
        var (page, rig, vm) = Show(c =>
        {
            c.Controller = new ControllerInfoConfig { ProtocolVersion = 1, Sliders = 5, Buttons = { IndicatorKind.Led, IndicatorKind.LedPair, IndicatorKind.Screen } };
            DefaultOf(c, 0).SinglePress.Action = ButtonActionType.MuteCurrentOutput;
            DefaultOf(c, 1).SinglePress = new ButtonActionConfig
            {
                Action = ButtonActionType.ToggleOutputDevice,
                Device = new DeviceRefConfig { Id = "dev-1", FriendlyName = "Speakers" },
                DeviceB = new DeviceRefConfig { Id = "dev-2", FriendlyName = "Headphones" },
            };
        });
        using var _ = rig; using var __ = vm;

        Assert.True(In<Ellipse>(Card(page, 0), "Led").IsVisible);
        Assert.DoesNotContain("on", In<Ellipse>(Card(page, 0), "Led").Classes);
        Assert.Contains("on", In<Ellipse>(Card(page, 1), "LedA").Classes);      // Speakers is A
        Assert.DoesNotContain("on", In<Ellipse>(Card(page, 1), "LedB").Classes);

        rig.Engine.DeviceStates["dev-1"] = (0.5f, true);
        rig.Engine.RaiseDeviceVolumeChanged("dev-1", 0.5f, muted: true, isOwnChange: false);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("on", In<Ellipse>(Card(page, 0), "Led").Classes);
        Assert.Equal("On while muted. Follows the Press action.", vm.Buttons.Items[0].LightDescription);
    }

    private static void Hardware(AppConfig c, params IndicatorKind[] kinds)
        => c.Controller = new ControllerInfoConfig { ProtocolVersion = 1, Sliders = 5, Buttons = kinds.ToList() };

    [AvaloniaFact]
    public void ButtonsPage_ScreenButton_ShowsAScreenPreviewOfTheEditedPreset()
    {
        var (page, rig, vm) = Show(c =>
        {
            Hardware(c, IndicatorKind.Screen, IndicatorKind.Led, IndicatorKind.None);
            DefaultOf(c, 0).SinglePress.Action = ButtonActionType.MuteCurrentOutput;
        });
        using var _ = rig; using var __ = vm;
        Expand(vm, 0);

        var small = In<ScreenPreview>(Card(page, 0), "ScreenPreviewSmall");
        Assert.True(small.IsVisible);
        Assert.Equal("Speakers", small.Screen!.Main);
        Assert.True(In<ScreenPreview>(Card(page, 0), "ScreenPreviewLarge").IsVisible);
        Assert.Equal("Light: Screen", In<TextBlock>(Card(page, 0), "IndicatorText").Text);
        Assert.False(In<ScreenPreview>(Card(page, 1), "ScreenPreviewSmall").IsVisible);
        Assert.True(In<Ellipse>(Card(page, 1), "Led").IsVisible);
        Assert.Equal("Light: None", vm.Buttons.Items[2].IndicatorText);
    }

    [AvaloniaFact]
    public void ButtonsPage_NoControllerInfo_AsksToConnect_AndShowsNoPreview()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        Expand(vm, 0);
        Assert.Equal("Connect your controller to see its lights and screens.", In<TextBlock>(Card(page, 0), "IndicatorText").Text);
        Assert.False(In<Ellipse>(Card(page, 0), "Led").IsVisible);
        Assert.False(In<ScreenPreview>(Card(page, 0), "ScreenPreviewSmall").IsVisible);
    }

    [AvaloniaFact]
    public void ButtonsPage_CountHint_AddsTheMissingButtons()
    {
        var (page, rig, vm) = Show(c => Hardware(c, IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair, IndicatorKind.Screen));
        using var _ = rig; using var __ = vm;
        Assert.Equal("Your controller has 4 buttons; 3 are set up.", In<TextBlock>(page, "CountHintText").Text);
        In<Button>(page, "AddMissingButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(4, rig.Config.Config.Buttons.Count);
        Assert.False(vm.Buttons.HasCountHint);
    }

    [AvaloniaFact]
    public void ButtonsPage_ControllerInfoArrives_CardsFollow()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        rig.Connection!.SetStatus(Volco.Core.Communication.ConnectionStatus.Connected);
        rig.Connection.RaiseMessage(new HelloMessage(ProtocolVersion.Current, 5,
            new[] { IndicatorKind.Screen, IndicatorKind.Led, IndicatorKind.Led }));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.Buttons.Items[0].IsScreen);
        Assert.True(In<ScreenPreview>(Card(page, 0), "ScreenPreviewSmall").IsVisible);
    }

    [AvaloniaFact]
    public void ButtonsPage_DragPreview_KeepsEachCardsOwnScreen()
    {
        var (_, rig, vm) = Show(c =>
        {
            Hardware(c, IndicatorKind.Screen, IndicatorKind.Led);
            DefaultOf(c, 0).SinglePress.Action = ButtonActionType.MuteCurrentOutput;
            DefaultOf(c, 1).SinglePress.Action = ButtonActionType.MuteCurrentInput;
        });
        using var _ = rig; using var __ = vm;
        var card0 = vm.Buttons.Items[0];
        var before = card0.Screen;
        Assert.True(card0.IsScreen);

        vm.Buttons.PreviewMove(0, 1);

        Assert.Equal(before, card0.Screen);
        Assert.Equal(IndicatorKind.Led, card0.Indicator);

        vm.Buttons.CancelPreview();

        Assert.Equal(before, card0.Screen);
        Assert.Equal(IndicatorKind.Screen, card0.Indicator);
    }

    // ── Presets ───────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void ButtonsPage_OnePreset_ShowsOnlyAnAddLink()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;

        Assert.True(page.FindControl<Button>("AddFirstPresetButton")!.IsEffectivelyVisible);
        Assert.False(page.FindControl<ItemsControl>("PresetTabs")!.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void ButtonsPage_AddPreset_CopiesTheEditedPreset_AndEditsIt()
    {
        var (page, rig, vm) = Show(c => DefaultOf(c, 0).SinglePress.Action = ButtonActionType.MuteCurrentInput);
        using var _ = rig; using var __ = vm;

        vm.Buttons.AddPresetCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var c = rig.Config.Config;
        Assert.Equal(2, c.Presets.Count);
        var added = c.Presets[1];
        Assert.Equal("Preset 2", added.Name);
        Assert.Equal(added.Id, vm.Buttons.EditingPresetId);
        Assert.Equal(ButtonActionType.MuteCurrentInput, c.Buttons[0].BindingFor(added.Id).SinglePress.Action);
        Assert.NotSame(c.Buttons[0].Default().SinglePress, c.Buttons[0].BindingFor(added.Id).SinglePress);
        Assert.True(page.FindControl<ItemsControl>("PresetTabs")!.IsEffectivelyVisible);
        Assert.True(vm.Buttons.IsRenaming);
    }

    [AvaloniaFact]
    public void ButtonsPage_EditingAPresetThatIsNotLive_ShowsTheBanner_AndMakeLiveSwitchesIt()
    {
        var (page, rig, vm) = Show(c => c.Presets.Add(new PresetConfig { Id = "gaming", Name = "Gaming" }));
        using var _ = rig; using var __ = vm;
        var buttons = vm.Buttons;
        Assert.Equal("Default is live on the controller.", page.FindControl<TextBlock>("BannerText")!.Text);
        Assert.False(page.FindControl<Button>("MakeLiveButton")!.IsVisible);

        buttons.Presets.Single(p => p.Id == "gaming").SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("You are editing Gaming. The controller is using Default.",
            page.FindControl<TextBlock>("BannerText")!.Text);
        var makeLive = page.FindControl<Button>("MakeLiveButton")!;
        Assert.True(makeLive.IsVisible);
        Assert.Equal("Make Gaming live", makeLive.Content);

        makeLive.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("gaming", rig.Runtime.ActivePresetId);
        Assert.Equal("gaming", buttons.LivePresetId);
        Assert.True(buttons.Presets.Single(p => p.Id == "gaming").IsLive);
    }

    [AvaloniaFact]
    public void ButtonsPage_SwitchingTheEditedPreset_ShowsThatPresetsActions()
    {
        var (page, rig, vm) = Show(c =>
        {
            c.Presets.Add(new PresetConfig { Id = "gaming", Name = "Gaming" });
            DefaultOf(c, 0).SinglePress.Action = ButtonActionType.MuteCurrentOutput;
            c.Buttons[0].GetOrAddBinding("gaming").SinglePress.Action = ButtonActionType.MuteCurrentInput;
        });
        using var _ = rig; using var __ = vm;
        Assert.Equal("Mute output", All<TextBlock>(page, "SummaryText")[0].Text);

        vm.Buttons.EditingPresetId = "gaming";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Mute mic", All<TextBlock>(page, "SummaryText")[0].Text);
        Assert.Equal(ButtonActionType.MuteCurrentInput, vm.Buttons.Items[0].Press.SelectedChoice.Type);

        // Editing now writes to Gaming only.
        vm.Buttons.Items[0].Press.SelectedChoice = ActionCatalog.For(ButtonActionType.NextPreset);
        Assert.Equal(ButtonActionType.NextPreset, rig.Config.Config.Buttons[0].BindingFor("gaming").SinglePress.Action);
        Assert.Equal(ButtonActionType.MuteCurrentOutput, rig.Config.Config.Buttons[0].Default().SinglePress.Action);
    }

    [AvaloniaFact]
    public void ButtonsPage_RenamePreset_CommitsTheNewName()
    {
        var (page, rig, vm) = Show(c => c.Presets.Add(new PresetConfig { Id = "gaming", Name = "Gaming" }));
        using var _ = rig; using var __ = vm;

        vm.Buttons.StartRenameCommand.Execute(null);
        vm.Buttons.RenameText = "Everyday";
        vm.Buttons.CommitRenameCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Everyday", rig.Config.Config.Presets[0].Name);
        Assert.Equal("Everyday", vm.Buttons.Presets[0].Name);
        Assert.False(vm.Buttons.IsRenaming);
    }

    [AvaloniaFact]
    public void ButtonsPage_DeleteLivePreset_FallsBackToTheFirst_AndUndoRestoresIt()
    {
        var (page, rig, vm) = Show(c =>
        {
            c.Presets.Add(new PresetConfig { Id = "gaming", Name = "Gaming" });
            c.Buttons[0].GetOrAddBinding("gaming").SinglePress.Action = ButtonActionType.MuteCurrentInput;
        });
        using var _ = rig; using var __ = vm;
        var buttons = vm.Buttons;
        rig.Runtime.SetActivePreset("gaming");
        Dispatcher.UIThread.RunJobs();
        buttons.EditingPresetId = "gaming";

        buttons.DeletePresetCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(rig.Config.Config.Presets);
        Assert.False(rig.Config.Config.Buttons[0].Actions.ContainsKey("gaming"));
        Assert.Equal(AppConfig.DefaultPresetId, rig.Runtime.ActivePresetId);
        Assert.Equal(AppConfig.DefaultPresetId, buttons.LivePresetId);
        Assert.False(buttons.HasMultiplePresets);
        Assert.Equal("Preset “Gaming” deleted", vm.Toast.Message);

        vm.Toast.UndoCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, rig.Config.Config.Presets.Count);
        Assert.Equal(ButtonActionType.MuteCurrentInput, rig.Config.Config.Buttons[0].BindingFor("gaming").SinglePress.Action);
        Assert.Equal("gaming", rig.Runtime.ActivePresetId);
    }

    // ── Add / reset / reorder ─────────────────────────────────────────────────

    [AvaloniaFact]
    public void ButtonsPage_Reset_ClearsActionsInEveryPreset_AndUndoRestoresThem()
    {
        var (page, rig, vm) = Show(c =>
        {
            c.Presets.Add(new PresetConfig { Id = "gaming", Name = "Gaming" });
            DefaultOf(c, 1).SinglePress.Action = ButtonActionType.MuteCurrentOutput;
            c.Buttons[1].GetOrAddBinding("gaming").LongPress.Action = ButtonActionType.NextPreset;
            c.Buttons[1].LongPressMs = 900;
        });
        using var _ = rig; using var __ = vm;
        var cfg = rig.Config.Config.Buttons[1];

        vm.Buttons.Items[1].ResetCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(cfg.Actions.Values.All(b => b.IsEmpty));
        Assert.Equal(500, cfg.LongPressMs);
        Assert.Equal("Not set up", All<TextBlock>(page, "SummaryText")[1].Text);
        Assert.Equal("Button 1 reset", vm.Toast.Message);

        vm.Toast.UndoCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(ButtonActionType.MuteCurrentOutput, cfg.Default().SinglePress.Action);
        Assert.Equal(ButtonActionType.NextPreset, cfg.BindingFor("gaming").LongPress.Action);
        Assert.Equal(900, cfg.LongPressMs);
    }

    [AvaloniaFact]
    public void ButtonsPage_Move_CarriesTheActionsOfEveryPreset()
    {
        var (page, rig, vm) = Show(c =>
        {
            c.Presets.Add(new PresetConfig { Id = "gaming", Name = "Gaming" });
            DefaultOf(c, 0).SinglePress.Action = ButtonActionType.MuteCurrentOutput;
            c.Buttons[0].GetOrAddBinding("gaming").SinglePress.Action = ButtonActionType.MuteCurrentInput;
        });
        using var _ = rig; using var __ = vm;

        vm.Buttons.Move(0, 2);
        Dispatcher.UIThread.RunJobs();

        var moved = rig.Config.Config.Buttons[2];
        Assert.Equal(ButtonActionType.MuteCurrentOutput, moved.Default().SinglePress.Action);
        Assert.Equal(ButtonActionType.MuteCurrentInput, moved.BindingFor("gaming").SinglePress.Action);
        Assert.Equal("Mute output", All<TextBlock>(page, "SummaryText")[2].Text);
        Assert.Equal(new[] { "Button 0", "Button 1", "Button 2" },
            All<TextBlock>(page, "TitleText").Select(t => t.Text).ToArray());
    }

    [AvaloniaFact]
    public void ButtonsPage_AddAndRemoveLast()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;

        page.FindControl<Button>("AddButton")!.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(4, rig.Config.Config.Buttons.Count);
        Assert.Equal("Button 3", All<TextBlock>(page, "TitleText")[3].Text);
        Assert.True(vm.Buttons.Items[3].IsLast);

        vm.Buttons.Items[3].RemoveLastCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, rig.Config.Config.Buttons.Count);
        Assert.Equal("Button 3 removed", vm.Toast.Message);
    }

    [AvaloniaFact]
    public void ButtonsPage_ButtonActivity_FlashesThatCard()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;

        vm.Buttons.Pulse(2);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("active", Card(page, 2).Classes);
        Assert.DoesNotContain("active", Card(page, 0).Classes);
    }

    [AvaloniaFact]
    public void DevicePicker_ShowsNicknames_WithTheWindowsNameAsSecondaryText()
    {
        var (page, rig, vm) = Show(c =>
        {
            c.Devices.Add(new DeviceSettingsConfig { Id = "dev-2", Nickname = "Cans" });
            DefaultOf(c, 0).SinglePress.Action = ButtonActionType.MuteSpecificDevice;
        });
        using var _ = rig; using var __ = vm;
        Expand(vm, 0);
        var slot = vm.Buttons.Items[0].Press;
        Assert.Equal(new DeviceChoice("Cans", "Headphones"), ((IDeviceLabeler)slot).Describe("Headphones"));
        Assert.Equal(new DeviceChoice("Speakers", ""), ((IDeviceLabeler)slot).Describe("Speakers"));
    }

    [AvaloniaFact]
    public void CardSummary_UsesNicknames()
    {
        var (_, rig, vm) = Show(c =>
        {
            c.Devices.Add(new DeviceSettingsConfig { Id = "dev-1", Nickname = "Desk" });
            DefaultOf(c, 0).SinglePress = new ButtonActionConfig
            {
                Action = ButtonActionType.SetOutputDevice, Device = new DeviceRefConfig { Id = "dev-1", FriendlyName = "Speakers" },
            };
        });
        using var _ = rig; using var __ = vm;
        Assert.Equal("Switch to Desk", vm.Buttons.Items[0].Summary);
    }

    [AvaloniaFact]
    public void DevicePicker_RenderedSelectionBox_ShowsTheNickname()
    {
        var (page, rig, vm) = Show(c =>
        {
            c.Devices.Add(new DeviceSettingsConfig { Id = "dev-2", Nickname = "Cans" });
            DefaultOf(c, 0).SinglePress = new ButtonActionConfig
            {
                Action = ButtonActionType.MuteSpecificDevice, Device = new DeviceRefConfig { Id = "dev-2", FriendlyName = "Headphones" },
            };
        });
        using var _ = rig; using var __ = vm;
        Expand(vm, 0);
        var combo = InRow<ComboBox>(page, "PressRow", "DeviceCombo");
        Assert.Equal("Headphones", combo.SelectedItem);
        var texts = combo.GetVisualDescendants().OfType<TextBlock>().ToList();
        Assert.Equal("Cans", texts.First(t => t.Name == "DeviceLabel").Text);
        Assert.Equal("Headphones", texts.First(t => t.Name == "DeviceSecondary").Text);
    }

    [AvaloniaFact]
    public void DevicePicker_RenderedDropdownItems_ShowNicknames()
    {
        var (page, rig, vm) = Show(c =>
        {
            c.Devices.Add(new DeviceSettingsConfig { Id = "dev-2", Nickname = "Cans" });
            DefaultOf(c, 0).SinglePress.Action = ButtonActionType.MuteSpecificDevice;
        });
        using var _ = rig; using var __ = vm;
        Expand(vm, 0);
        var combo = InRow<ComboBox>(page, "PressRow", "DeviceCombo");
        combo.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();
        var labels = new List<string?>();
        for (int i = 0; i < combo.ItemCount; i++)
        {
            var container = Assert.IsAssignableFrom<Control>(combo.ContainerFromIndex(i));
            labels.Add(container.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "DeviceLabel").Text);
        }
        Assert.Contains("Cans", labels);
        Assert.Contains("Speakers", labels);
        Assert.DoesNotContain("Headphones", labels);
    }
}
