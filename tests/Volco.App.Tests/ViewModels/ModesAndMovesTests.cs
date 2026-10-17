using Volco.App.ViewModels;
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.App.Tests.ViewModels;

/// <summary>The modes (presets in code) and the moves of <see cref="ButtonsViewModel"/> and
/// <see cref="SlidersViewModel"/>, which the Controls page reuses: Add mode, Make live, Rename and
/// Delete today, drag to swap once its control comes back (spec controls-page §13 step 4).</summary>
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

    [Fact]
    public void AddPreset_CopiesTheEditedPreset_AndEditsIt()
    {
        Config.Buttons[0].Default().SinglePress.Action = ButtonActionType.MuteCurrentInput;
        var buttons = Buttons();

        buttons.AddPresetCommand.Execute(null);

        Assert.Equal(3, Config.Presets.Count);
        var added = Config.Presets[2];
        Assert.Equal("Mode 3", added.Name);
        Assert.Equal(added.Id, buttons.EditingPresetId);
        Assert.Equal(ButtonActionType.MuteCurrentInput, Config.Buttons[0].BindingFor(added.Id).SinglePress.Action);
        Assert.NotSame(Config.Buttons[0].Default().SinglePress, Config.Buttons[0].BindingFor(added.Id).SinglePress);
        Assert.Equal(3, buttons.Presets.Count);
        Assert.True(buttons.Presets[2].IsEditing);
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
    public void RenamePreset_CommitsTheNewName()
    {
        var buttons = Buttons();

        buttons.StartRenameCommand.Execute(null);
        buttons.RenameText = "Everyday";
        buttons.CommitRenameCommand.Execute(null);

        Assert.Equal("Everyday", Config.Presets[0].Name);
        Assert.Equal("Everyday", buttons.Presets[0].Name);
        Assert.Equal("Everyday", buttons.EditingName);
        Assert.False(buttons.IsRenaming);
    }

    [Fact]
    public void DeletingAPreset_EditsThePresetBefore_AndMovesTheColoursAfter()
    {
        Config.Presets.Add(new PresetConfig { Id = "work", Name = "Work" });
        var buttons = Buttons();

        buttons.EditingPresetId = "gaming";
        buttons.DeletePresetCommand.Execute(null);
        Assert.Equal(AppConfig.DefaultPresetId, buttons.EditingPresetId);
        Assert.Equal(new[] { 0, 1 }, buttons.Presets.Select(p => p.TintIndex));
        buttons.EditingPresetId = "work";
        Assert.Equal(1, buttons.EditingTintIndex);

        // The first has none before it: the new first is edited.
        buttons.EditingPresetId = AppConfig.DefaultPresetId;
        buttons.DeletePresetCommand.Execute(null);
        Assert.Equal("work", buttons.EditingPresetId);
        Assert.True(buttons.Presets.Single().IsEditing);
    }

    [Fact]
    public void DeleteLivePreset_FallsBackToTheFirst_AndUndoRestoresIt()
    {
        Config.Buttons[0].GetOrAddBinding("gaming").SinglePress.Action = ButtonActionType.MuteCurrentInput;
        _rig.Runtime.SetActivePreset("gaming");
        var buttons = Buttons();
        Assert.Equal("gaming", buttons.EditingPresetId);

        buttons.DeletePresetCommand.Execute(null);

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
