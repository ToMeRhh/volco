using Volco.App.ViewModels;
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.App.Tests.ViewModels;

/// <summary>The modes of <see cref="ButtonsViewModel"/>, which the Controls page reuses: Add mode, copy,
/// Make live, Rename, Delete and reorder (swaps are in SwapTests).</summary>
public sealed class ModesAndMovesTests : IDisposable
{
    private readonly TestRig _rig;
    private readonly UndoToastViewModel _toast = new();

    public ModesAndMovesTests()
    {
        var speakers = new AudioDevice("dev-1", "Speakers", AudioFlow.Render, true);
        _rig = new TestRig(new FakeAudioEngine { Devices = { speakers }, DefaultRender = speakers });
        Config.Modes.Add(new ModeConfig { Id = "gaming", Name = "Gaming" });
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

        Assert.Equal(3, Config.Modes.Count);
        var added = Config.Modes[2];
        Assert.Equal("Mode 2", added.Name);
        Assert.All(Config.Buttons, b => Assert.True(NoBinding(b, added.Id)));
        Assert.Equal(added.Id, buttons.EditingModeId);
        Assert.Equal(AppConfig.DefaultModeId, buttons.LiveModeId);
        Assert.Equal(added.Id, buttons.MenuModeId);
        Assert.Equal(3, buttons.Modes.Count);
        Assert.True(buttons.Modes[2].IsEditing);
    }

    [Fact]
    public void AddMode_NameSkipsTakenNumbers()
    {
        Config.Modes.Add(new ModeConfig { Id = "m2", Name = "Mode 2" });
        Config.Modes.Add(new ModeConfig { Id = "m3", Name = "Mode 3" });
        var buttons = Buttons();

        buttons.AddModeCommand.Execute(null);
        Assert.Equal("Mode 4", Config.Modes[^1].Name);

        buttons.DeleteMode("m2");
        buttons.AddModeCommand.Execute(null);
        Assert.Equal("Mode 2", Config.Modes[^1].Name);
    }

    [Fact]
    public void CopyActions_ReplacesTarget_UndoRestores()
    {
        Config.Buttons[0].Default().SinglePress.Action = ButtonActionType.MuteCurrentInput;
        Config.Buttons[1].GetOrAddBinding("gaming").SinglePress.Action = ButtonActionType.MediaPlayPause;
        var buttons = Buttons();

        buttons.CopyActions(AppConfig.DefaultModeId, "gaming");

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

        buttons.CopyActions(AppConfig.DefaultModeId, "gaming");
        Config.Buttons[0].BindingFor("gaming").SinglePress.Device = new DeviceRefConfig { Id = "dev-2", FriendlyName = "Cans" };

        Assert.Equal("dev-1", Config.Buttons[0].Default().SinglePress.Device!.Id);
    }

    [Fact]
    public void MakeLive_MakesTheEditedModeTheControllers()
    {
        var buttons = Buttons();
        Assert.True(buttons.IsEditingLive);

        buttons.Modes.Single(p => p.Id == "gaming").SelectCommand.Execute(null);
        Assert.False(buttons.IsEditingLive);
        Assert.Equal("Make Gaming live", buttons.MakeLiveText);

        buttons.MakeLiveCommand.Execute(null);
        Assert.Equal("gaming", _rig.Runtime.ActiveModeId);
    }

    [Fact]
    public void SwitchingTheEditedMode_ShowsAndEditsThatModesActions()
    {
        Config.Buttons[0].Default().SinglePress.Action = ButtonActionType.MuteCurrentOutput;
        Config.Buttons[0].GetOrAddBinding("gaming").SinglePress.Action = ButtonActionType.MuteCurrentInput;
        var buttons = Buttons();
        var item = buttons.Items[0];
        Assert.Equal("Mute output", item.MainLabel);

        buttons.EditingModeId = "gaming";

        Assert.Equal("Mute mic", item.MainLabel);
        Assert.Equal(ButtonActionType.MuteCurrentInput, item.Press.SelectedChoice.Type);
        item.Press.SelectedChoice = ActionCatalog.For(ButtonActionType.NextMode);
        Assert.Equal(ButtonActionType.NextMode, Config.Buttons[0].BindingFor("gaming").SinglePress.Action);
        Assert.Equal(ButtonActionType.MuteCurrentOutput, Config.Buttons[0].Default().SinglePress.Action);
    }

    [Fact]
    public void Rename_EmptyOrSame_DoesNothing()
    {
        var buttons = Buttons();

        buttons.RenameMode("gaming", "   ");
        buttons.RenameMode("gaming", "Gaming");

        Assert.Equal("Gaming", Config.Modes[1].Name);
        Assert.Equal("Gaming", buttons.Modes[1].Name);
    }

    [Fact]
    public void Rename_UpdatesTabAndPickers()
    {
        var buttons = Buttons();

        buttons.StartRename("gaming");
        Assert.Equal("Gaming", buttons.RenameText);
        Assert.True(buttons.Modes[1].IsRenaming);
        buttons.RenameText = "  Play ";
        buttons.CommitRename();

        Assert.Equal("Play", Config.Modes[1].Name);
        Assert.Equal("Play", buttons.Modes[1].Name);
        Assert.Contains(buttons.ModeChoices(), c => c.Id == "gaming" && c.Name == "Play");
        Assert.Null(buttons.RenamingModeId);
        Assert.False(buttons.Modes[1].IsRenaming);

        buttons.StartRename("gaming");
        buttons.CancelRename();
        Assert.Null(buttons.RenamingModeId);
        Assert.Equal("Play", Config.Modes[1].Name);
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
        Assert.False(buttons.Modes[1].IsRenaming);
    }

    [Fact]
    public void DeletingAMode_EditsTheModeBefore_AndMovesTheColoursAfter()
    {
        Config.Modes.Add(new ModeConfig { Id = "work", Name = "Work" });
        var buttons = Buttons();

        buttons.EditingModeId = "gaming";
        buttons.DeleteMode("gaming");
        Assert.Equal(AppConfig.DefaultModeId, buttons.EditingModeId);
        Assert.Equal(new[] { 0, 1 }, buttons.Modes.Select(p => p.TintIndex));
        buttons.EditingModeId = "work";
        Assert.Equal(1, buttons.EditingTintIndex);
    }

    [Fact]
    public void DeleteMode_FirstIsRefused()
    {
        var buttons = Buttons();

        buttons.DeleteMode(AppConfig.DefaultModeId);

        Assert.Equal(2, Config.Modes.Count);
        Assert.False(buttons.Modes[0].CanDelete);
        Assert.True(buttons.Modes[0].IsFirst);
        Assert.True(buttons.Modes[1].CanDelete);
    }

    [Fact]
    public void DeleteMode_Live_FallsBackToFirst_UndoRestoresLive()
    {
        Config.Buttons[0].GetOrAddBinding("gaming").SinglePress.Action = ButtonActionType.MuteCurrentInput;
        _rig.Runtime.SetActiveMode("gaming");
        var buttons = Buttons();
        Assert.Equal("gaming", buttons.EditingModeId);

        buttons.Modes[1].DeleteCommand.Execute(null);

        Assert.Single(Config.Modes);
        Assert.False(Config.Buttons[0].Actions.ContainsKey("gaming"));
        Assert.Equal(AppConfig.DefaultModeId, _rig.Runtime.ActiveModeId);
        Assert.Equal(AppConfig.DefaultModeId, buttons.LiveModeId);
        Assert.Equal("Mode “Gaming” deleted", _toast.Message);

        _toast.UndoCommand.Execute(null);

        Assert.Equal(2, Config.Modes.Count);
        Assert.Equal(ButtonActionType.MuteCurrentInput, Config.Buttons[0].BindingFor("gaming").SinglePress.Action);
        Assert.Equal("gaming", _rig.Runtime.ActiveModeId);
        Assert.Equal("gaming", buttons.EditingModeId);
    }

    [Fact]
    public void GoToMode_TargetDeleted_ShowsProblem()
    {
        Config.Buttons[0].Default().SinglePress = new ButtonActionConfig { Action = ButtonActionType.GoToMode, ModeId = "gaming" };
        var buttons = Buttons();
        Assert.False(buttons.Items[0].Press.HasProblem);

        buttons.DeleteMode("gaming");

        Assert.True(buttons.Items[0].Press.HasProblem);
    }

    [Fact]
    public void DeleteThenAddMode_DoesNotReuseId_UndoAddsNoDuplicate()
    {
        Config.Modes.Add(new ModeConfig { Id = "mode-3", Name = "Mode 3" });
        var buttons = Buttons();

        buttons.DeleteMode("mode-3");
        buttons.AddModeCommand.Execute(null);
        var added = Config.Modes[^1].Id;
        Assert.NotEqual("mode-3", added);

        _toast.UndoCommand.Execute(null);

        Assert.Single(Config.Modes, m => m.Id == "mode-3");
        Assert.Single(Config.Modes, m => m.Id == added);
        Assert.Equal(Config.Modes.Count, Config.Modes.Select(m => m.Id).Distinct().Count());
    }

    [Fact]
    public void NewModeId_SkipsDanglingGoToModeIds()
    {
        Config.Buttons[0].Default().SinglePress = new ButtonActionConfig { Action = ButtonActionType.GoToMode, ModeId = "mode-2" };
        var buttons = Buttons();
        Assert.True(buttons.Items[0].Press.HasProblem);

        buttons.AddModeCommand.Execute(null);

        Assert.NotEqual("mode-2", Config.Modes[^1].Id);
        buttons.EditingModeId = AppConfig.DefaultModeId;
        Assert.True(buttons.Items[0].Press.HasProblem);
    }

    [Fact]
    public void MoveMode_KeepsLiveAndEdited_RecoloursTabs()
    {
        Config.Modes.Add(new ModeConfig { Id = "work", Name = "Work" });
        var buttons = Buttons();
        buttons.EditingModeId = "gaming";

        buttons.MoveMode(2, 0);

        Assert.Equal(AppConfig.DefaultModeId, buttons.LiveModeId);
        Assert.Equal("gaming", buttons.EditingModeId);
        Assert.Equal("work", buttons.Modes[0].Id);
        Assert.Equal(new[] { 0, 1, 2 }, buttons.Modes.Select(p => p.TintIndex));
        Assert.Equal(2, buttons.EditingTintIndex);
        Assert.True(buttons.Modes[2].IsEditing);
        Assert.True(buttons.Modes[1].IsLive);
    }

    [Fact]
    public void MoveMode_ToFirst_MakesItTheStartupMode()
    {
        var buttons = Buttons();

        buttons.MoveMode(1, 0);
        Assert.Equal("gaming", Config.Modes[0].Id);
        Assert.True(buttons.Modes[0].IsFirst);

        buttons.MoveMode(0, 0);
        buttons.MoveMode(-1, 0);
        buttons.MoveMode(0, 5);
        Assert.Equal(new[] { "gaming", AppConfig.DefaultModeId }, Config.Modes.Select(p => p.Id));
    }
}
