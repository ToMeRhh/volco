using Volco.App.ViewModels;
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.App.Tests.ViewModels;

/// <summary>The modes (presets in code) and the moves of <see cref="ButtonsViewModel"/> and
/// <see cref="SlidersViewModel"/>, which the Controls page reuses: Add mode, copy, Make live, Rename,
/// Delete and reorder, drag to swap once its control comes back (spec controls-page §13 step 4).</summary>
public sealed class ModesAndMovesTests : IDisposable
{
    private readonly TestRig _rig;
    private readonly UndoToastViewModel _toast = new();

    public ModesAndMovesTests()
    {
        var speakers = new AudioDevice("dev-1", "Speakers", AudioFlow.Render, true);
        _rig = new TestRig(new FakeAudioEngine { Devices = { speakers }, DefaultRender = speakers });
        Config.Presets.Add(new PresetConfig { Id = "gaming", Name = "Gaming" });
    }

    public void Dispose() => _rig.Dispose();

    private AppConfig Config => _rig.Config.Config;

    private ButtonsViewModel Buttons() => new(_rig.Config, _rig.Engine, _rig.Runtime, _rig.Runtime.Execute, _toast);

    // ── Modes ─────────────────────────────────────────────────────────────────

    private static bool NoBinding(ButtonConfig button, string id)
        => !button.Actions.TryGetValue(id, out var b) || b is null || b.IsEmpty;

    [Fact]
    public void AddMode_IsEmpty_EditedNotLive_OpensMenu()
    {
        Config.Buttons[0].Default().SinglePress.Action = ButtonActionType.MuteCurrentInput;
        var buttons = Buttons();

        buttons.AddModeCommand.Execute(null);

        Assert.Equal(3, Config.Presets.Count);
        var added = Config.Presets[2];
        Assert.Equal("Mode 2", added.Name);
        Assert.All(Config.Buttons, b => Assert.True(NoBinding(b, added.Id)));
        Assert.Equal(added.Id, buttons.EditingPresetId);
        Assert.Equal(AppConfig.DefaultPresetId, buttons.LivePresetId);
        Assert.Equal(added.Id, buttons.MenuModeId);
        Assert.Equal(3, buttons.Presets.Count);
        Assert.True(buttons.Presets[2].IsEditing);
    }

    [Fact]
    public void AddMode_NameSkipsTakenNumbers()
    {
        Config.Presets.Add(new PresetConfig { Id = "m2", Name = "Mode 2" });
        Config.Presets.Add(new PresetConfig { Id = "m3", Name = "Mode 3" });
        var buttons = Buttons();

        buttons.AddModeCommand.Execute(null);
        Assert.Equal("Mode 4", Config.Presets[^1].Name);

        buttons.DeleteMode("m2");
        buttons.AddModeCommand.Execute(null);
        Assert.Equal("Mode 2", Config.Presets[^1].Name);
    }

    [Fact]
    public void CopyActions_ReplacesTarget_UndoRestores()
    {
        Config.Buttons[0].Default().SinglePress.Action = ButtonActionType.MuteCurrentInput;
        Config.Buttons[1].GetOrAddBinding("gaming").SinglePress.Action = ButtonActionType.MediaPlayPause;
        var buttons = Buttons();

        buttons.CopyActions(AppConfig.DefaultPresetId, "gaming");

        Assert.Equal(ButtonActionType.MuteCurrentInput, Config.Buttons[0].BindingFor("gaming").SinglePress.Action);
        Assert.True(NoBinding(Config.Buttons[1], "gaming"));
        Assert.Equal("Copied “Default” into “Gaming”", _toast.Message);

        _toast.UndoCommand.Execute(null);

        Assert.True(NoBinding(Config.Buttons[0], "gaming"));
        Assert.Equal(ButtonActionType.MediaPlayPause, Config.Buttons[1].BindingFor("gaming").SinglePress.Action);
    }

    [Fact]
    public void CopyActions_IsADeepCopy()
    {
        Config.Buttons[0].Default().SinglePress = new ButtonActionConfig
        {
            Action = ButtonActionType.MuteSpecificDevice,
            Device = new DeviceRefConfig { Id = "dev-1", FriendlyName = "Speakers" },
        };
        var buttons = Buttons();

        buttons.CopyActions(AppConfig.DefaultPresetId, "gaming");
        Config.Buttons[0].BindingFor("gaming").SinglePress.Device = new DeviceRefConfig { Id = "dev-2", FriendlyName = "Cans" };

        Assert.Equal("dev-1", Config.Buttons[0].Default().SinglePress.Device!.Id);
    }

    [Fact]
    public void MakeLive_MakesTheEditedPresetTheControllers()
    {
        var buttons = Buttons();
        Assert.True(buttons.IsEditingLive);

        buttons.Presets.Single(p => p.Id == "gaming").SelectCommand.Execute(null);
        Assert.False(buttons.IsEditingLive);
        Assert.Equal("Make Gaming live", buttons.MakeLiveText);

        buttons.MakeLiveCommand.Execute(null);
        Assert.Equal("gaming", _rig.Runtime.ActivePresetId);
    }

    [Fact]
    public void SwitchingTheEditedPreset_ShowsAndEditsThatPresetsActions()
    {
        Config.Buttons[0].Default().SinglePress.Action = ButtonActionType.MuteCurrentOutput;
        Config.Buttons[0].GetOrAddBinding("gaming").SinglePress.Action = ButtonActionType.MuteCurrentInput;
        var buttons = Buttons();
        var item = buttons.Items[0];
        Assert.Equal("Mute output", item.MainLabel);

        buttons.EditingPresetId = "gaming";

        Assert.Equal("Mute mic", item.MainLabel);
        Assert.Equal(ButtonActionType.MuteCurrentInput, item.Press.SelectedChoice.Type);
        item.Press.SelectedChoice = ActionCatalog.For(ButtonActionType.NextPreset);
        Assert.Equal(ButtonActionType.NextPreset, Config.Buttons[0].BindingFor("gaming").SinglePress.Action);
        Assert.Equal(ButtonActionType.MuteCurrentOutput, Config.Buttons[0].Default().SinglePress.Action);
    }

    [Fact]
    public void Rename_EmptyOrSame_DoesNothing()
    {
        var buttons = Buttons();

        buttons.RenameMode("gaming", "   ");
        buttons.RenameMode("gaming", "Gaming");

        Assert.Equal("Gaming", Config.Presets[1].Name);
        Assert.Equal("Gaming", buttons.Presets[1].Name);
    }

    [Fact]
    public void Rename_UpdatesTabAndPickers()
    {
        var buttons = Buttons();

        buttons.StartRename("gaming");
        Assert.Equal("Gaming", buttons.RenameText);
        Assert.True(buttons.Presets[1].IsRenaming);
        buttons.RenameText = "  Play ";
        buttons.CommitRename();

        Assert.Equal("Play", Config.Presets[1].Name);
        Assert.Equal("Play", buttons.Presets[1].Name);
        Assert.Contains(buttons.PresetChoices(), c => c.Id == "gaming" && c.Name == "Play");
        Assert.Null(buttons.RenamingModeId);
        Assert.False(buttons.Presets[1].IsRenaming);

        buttons.StartRename("gaming");
        buttons.CancelRename();
        Assert.Null(buttons.RenamingModeId);
        Assert.Equal("Play", Config.Presets[1].Name);
    }

    [Fact]
    public void ToggleEditModes_EndsRename()
    {
        var buttons = Buttons();

        buttons.ToggleEditModesCommand.Execute(null);
        Assert.True(buttons.IsEditingModes);
        buttons.StartRename("gaming");

        buttons.ToggleEditModesCommand.Execute(null);

        Assert.False(buttons.IsEditingModes);
        Assert.Null(buttons.RenamingModeId);
        Assert.False(buttons.Presets[1].IsRenaming);
    }

    [Fact]
    public void DeletingAMode_EditsTheModeBefore_AndMovesTheColoursAfter()
    {
        Config.Presets.Add(new PresetConfig { Id = "work", Name = "Work" });
        var buttons = Buttons();

        buttons.EditingPresetId = "gaming";
        buttons.DeleteMode("gaming");
        Assert.Equal(AppConfig.DefaultPresetId, buttons.EditingPresetId);
        Assert.Equal(new[] { 0, 1 }, buttons.Presets.Select(p => p.TintIndex));
        buttons.EditingPresetId = "work";
        Assert.Equal(1, buttons.EditingTintIndex);
    }

    [Fact]
    public void DeleteMode_FirstIsRefused()
    {
        var buttons = Buttons();

        buttons.DeleteMode(AppConfig.DefaultPresetId);

        Assert.Equal(2, Config.Presets.Count);
        Assert.False(buttons.Presets[0].CanDelete);
        Assert.True(buttons.Presets[0].IsFirst);
        Assert.True(buttons.Presets[1].CanDelete);
    }

    [Fact]
    public void DeleteMode_Live_FallsBackToFirst_UndoRestoresLive()
    {
        Config.Buttons[0].GetOrAddBinding("gaming").SinglePress.Action = ButtonActionType.MuteCurrentInput;
        _rig.Runtime.SetActivePreset("gaming");
        var buttons = Buttons();
        Assert.Equal("gaming", buttons.EditingPresetId);

        buttons.Presets[1].DeleteCommand.Execute(null);

        Assert.Single(Config.Presets);
        Assert.False(Config.Buttons[0].Actions.ContainsKey("gaming"));
        Assert.Equal(AppConfig.DefaultPresetId, _rig.Runtime.ActivePresetId);
        Assert.Equal(AppConfig.DefaultPresetId, buttons.LivePresetId);
        Assert.Equal("Mode “Gaming” deleted", _toast.Message);

        _toast.UndoCommand.Execute(null);

        Assert.Equal(2, Config.Presets.Count);
        Assert.Equal(ButtonActionType.MuteCurrentInput, Config.Buttons[0].BindingFor("gaming").SinglePress.Action);
        Assert.Equal("gaming", _rig.Runtime.ActivePresetId);
        Assert.Equal("gaming", buttons.EditingPresetId);
    }

    [Fact]
    public void GoToMode_TargetDeleted_ShowsProblem()
    {
        Config.Buttons[0].Default().SinglePress = new ButtonActionConfig { Action = ButtonActionType.GoToPreset, PresetId = "gaming" };
        var buttons = Buttons();
        Assert.False(buttons.Items[0].Press.HasProblem);

        buttons.DeleteMode("gaming");

        Assert.True(buttons.Items[0].Press.HasProblem);
    }

    [Fact]
    public void MoveMode_KeepsLiveAndEdited_RecoloursTabs()
    {
        Config.Presets.Add(new PresetConfig { Id = "work", Name = "Work" });
        var buttons = Buttons();
        buttons.EditingPresetId = "gaming";

        buttons.MoveMode(2, 0);

        Assert.Equal(AppConfig.DefaultPresetId, buttons.LivePresetId);
        Assert.Equal("gaming", buttons.EditingPresetId);
        Assert.Equal("work", buttons.Presets[0].Id);
        Assert.Equal(new[] { 0, 1, 2 }, buttons.Presets.Select(p => p.TintIndex));
        Assert.Equal(2, buttons.EditingTintIndex);
        Assert.True(buttons.Presets[2].IsEditing);
        Assert.True(buttons.Presets[1].IsLive);
    }

    [Fact]
    public void MoveMode_ToFirst_MakesItTheStartupMode()
    {
        var buttons = Buttons();

        buttons.MoveMode(1, 0);
        Assert.Equal("gaming", Config.Presets[0].Id);
        Assert.True(buttons.Presets[0].IsFirst);

        buttons.MoveMode(0, 0);
        buttons.MoveMode(-1, 0);
        buttons.MoveMode(0, 5);
        Assert.Equal(new[] { "gaming", AppConfig.DefaultPresetId }, Config.Presets.Select(p => p.Id));
    }

    // ── Moves: position is the hardware input ─────────────────────────────────

    [Fact]
    public void ButtonMove_CarriesTheActionsOfEveryPreset()
    {
        Config.Buttons[0].Default().SinglePress.Action = ButtonActionType.MuteCurrentOutput;
        Config.Buttons[0].GetOrAddBinding("gaming").SinglePress.Action = ButtonActionType.MuteCurrentInput;
        var buttons = Buttons();
        var moved = buttons.Items[0];

        buttons.Move(0, 2);

        Assert.Equal(ButtonActionType.MuteCurrentOutput, Config.Buttons[2].Default().SinglePress.Action);
        Assert.Equal(ButtonActionType.MuteCurrentInput, Config.Buttons[2].BindingFor("gaming").SinglePress.Action);
        Assert.Same(moved, buttons.Items[2]);
        Assert.Equal(new[] { 0, 1, 2 }, buttons.Items.Select(i => i.Index));
        Assert.Equal("Mute output", buttons.Items[2].MainLabel);
        Assert.True(buttons.Items[2].IsLast);
    }

    [Fact]
    public void SliderMove_MovesTheConfigToAnotherInput()
    {
        Config.Sliders[0].Type = SliderMappingType.Unmapped;
        var moved = Config.Sliders[0];
        var sliders = new SlidersViewModel(_rig.Config, _rig.Engine, _toast);

        sliders.Move(0, 1);

        Assert.Same(moved, Config.Sliders[1]);
        Assert.Equal("All other apps", sliders.Items[1].Label);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, sliders.Items.Select(i => i.Index));
    }
}
