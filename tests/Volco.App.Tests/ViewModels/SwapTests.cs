using Volco.App.ViewModels;
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.App.Tests.ViewModels;

/// <summary>Drag to swap, the model (spec docs/design/controls-page.md, step 4): a button swap moves
/// the edited mode's actions only, a slider swap moves the whole slider.</summary>
public sealed class SwapTests : IDisposable
{
    private readonly TestRig _rig;
    private readonly UndoToastViewModel _toast = new();

    public SwapTests()
    {
        var speakers = new AudioDevice("dev-1", "Speakers", AudioFlow.Render, true);
        _rig = new TestRig(new FakeAudioEngine { Devices = { speakers }, DefaultRender = speakers });
        Config.Presets.Add(new PresetConfig { Id = "gaming", Name = "Gaming" });
    }

    public void Dispose() => _rig.Dispose();

    private AppConfig Config => _rig.Config.Config;

    private ControlsViewModel Vm()
    {
        var sliders = new SlidersViewModel(_rig.Config, _rig.Engine, _toast);
        var buttons = new ButtonsViewModel(_rig.Config, _rig.Engine, _rig.Runtime, _rig.Runtime.Execute, _toast);
        return new ControlsViewModel(sliders, buttons, _rig.Config, _toast);
    }

    [Fact]
    public void SwapActions_EditedModeOnly_TimingStays()
    {
        Config.Buttons[0].Default().SinglePress.Action = ButtonActionType.MuteCurrentOutput;
        Config.Buttons[0].GetOrAddBinding("gaming").SinglePress.Action = ButtonActionType.MuteCurrentOutput;
        Config.Buttons[0].LongPressMs = 800;
        Config.Buttons[2].Default().SinglePress.Action = ButtonActionType.MediaPlayPause;
        Config.Buttons[2].LongPressMs = 500;
        var buttons = Vm().Buttons;

        buttons.SwapActions(0, 2);

        Assert.Equal(ButtonActionType.MediaPlayPause, Config.Buttons[0].Default().SinglePress.Action);
        Assert.Equal(ButtonActionType.MuteCurrentOutput, Config.Buttons[2].Default().SinglePress.Action);
        Assert.Equal(ButtonActionType.MuteCurrentOutput, Config.Buttons[0].BindingFor("gaming").SinglePress.Action);
        Assert.False(Config.Buttons[2].Actions.ContainsKey("gaming"));
        Assert.Equal(800, Config.Buttons[0].LongPressMs);
        Assert.Equal(500, Config.Buttons[2].LongPressMs);
        Assert.Equal("Swapped button 1 and button 3 in Default", _toast.Message);
    }

    [Fact]
    public void SwapActions_MissingKeyMoves()
    {
        Config.Buttons[0].Actions.Remove(AppConfig.DefaultPresetId);
        Config.Buttons[1].Default().SinglePress.Action = ButtonActionType.MuteCurrentInput;
        var buttons = Vm().Buttons;

        buttons.SwapActions(0, 1);

        Assert.Equal(ButtonActionType.MuteCurrentInput, Config.Buttons[0].Default().SinglePress.Action);
        Assert.True(Config.Buttons[1].BindingFor(AppConfig.DefaultPresetId).IsEmpty);
    }

    [Fact]
    public void SwapActions_Undo_SwapsBack()
    {
        Config.Buttons[0].Default().SinglePress.Action = ButtonActionType.MuteCurrentOutput;
        Config.Buttons[1].Actions.Remove(AppConfig.DefaultPresetId);
        var buttons = Vm().Buttons;

        buttons.SwapActions(0, 1);
        Assert.True(Config.Buttons[0].BindingFor(AppConfig.DefaultPresetId).IsEmpty);
        _toast.UndoCommand.Execute(null);

        Assert.Equal(ButtonActionType.MuteCurrentOutput, Config.Buttons[0].Default().SinglePress.Action);
        Assert.True(Config.Buttons[1].BindingFor(AppConfig.DefaultPresetId).IsEmpty);
    }

    [Fact]
    public void SwapActions_Undo_AfterSwitchingMode_StillUsesTheSwappedMode()
    {
        Config.Buttons[0].Default().SinglePress.Action = ButtonActionType.MuteCurrentOutput;
        Config.Buttons[1].Default().SinglePress.Action = ButtonActionType.MediaPlayPause;
        Config.Buttons[0].GetOrAddBinding("gaming").SinglePress.Action = ButtonActionType.MuteCurrentInput;
        var buttons = Vm().Buttons;

        buttons.SwapActions(0, 1);
        buttons.EditingPresetId = "gaming";
        _toast.UndoCommand.Execute(null);

        Assert.Equal(ButtonActionType.MuteCurrentOutput, Config.Buttons[0].Default().SinglePress.Action);
        Assert.Equal(ButtonActionType.MediaPlayPause, Config.Buttons[1].Default().SinglePress.Action);
        Assert.Equal(ButtonActionType.MuteCurrentInput, Config.Buttons[0].BindingFor("gaming").SinglePress.Action);
        Assert.True(Config.Buttons[1].BindingFor("gaming").IsEmpty);
    }

    [Fact]
    public void SliderSwap_MovesWholeConfig_Undo()
    {
        Config.Sliders[0].Type = SliderMappingType.Unmapped;
        var first = Config.Sliders[0];
        var second = Config.Sliders[1];
        var sliders = Vm().Sliders;
        var item0 = sliders.Items[0];
        var item1 = sliders.Items[1];

        sliders.Swap(0, 1);

        Assert.Same(first, Config.Sliders[1]);
        Assert.Same(second, Config.Sliders[0]);
        Assert.Same(item0, sliders.Items[1]);
        Assert.Same(item1, sliders.Items[0]);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, sliders.Items.Select(i => i.Index));
        Assert.Equal("Swapped slider 1 and slider 2", _toast.Message);

        _toast.UndoCommand.Execute(null);

        Assert.Same(first, Config.Sliders[0]);
        Assert.Same(second, Config.Sliders[1]);
        Assert.Same(item0, sliders.Items[0]);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, sliders.Items.Select(i => i.Index));
    }

    [Fact]
    public void Swap_MixedKindsOrMissing_DoesNothing_NoSave()
    {
        Config.Controller = new ControllerInfoConfig
        {
            ProtocolVersion = 1, Sliders = 2,
            Buttons = new List<IndicatorKind> { IndicatorKind.Led, IndicatorKind.Led },
        };
        Config.Buttons[0].Default().SinglePress.Action = ButtonActionType.MuteCurrentOutput;
        var slider0 = Config.Sliders[0];
        var vm = Vm();
        var s = vm.Sliders.Items;
        var b = vm.Buttons.Items;
        Assert.True(s[2].IsMissing);
        Assert.True(b[2].IsMissing);

        vm.Swap(s[0], b[0]);      // mixed kinds
        vm.Swap(s[0], s[0]);      // same item
        vm.Swap(s[0], s[2]);      // missing target
        vm.Swap(b[2], b[0]);      // missing source
        vm.Swap(b[0], b[2]);

        // The rig has no save hook (RequestSave is debounced), so: nothing changed and no toast.
        Assert.False(_toast.IsOpen);
        Assert.Same(slider0, Config.Sliders[0]);
        Assert.Same(slider0, s[0].Config);
        Assert.Equal(ButtonActionType.MuteCurrentOutput, Config.Buttons[0].Default().SinglePress.Action);
        Assert.True(Config.Buttons[2].BindingFor(AppConfig.DefaultPresetId).IsEmpty);
    }

    [Fact]
    public void Swap_SelectsTarget()
    {
        var vm = Vm();

        vm.Swap(vm.Sliders.Items[0], vm.Sliders.Items[3]);
        Assert.Same(vm.Sliders.Items[3], vm.Selected);

        vm.Swap(vm.Buttons.Items[2], vm.Buttons.Items[1]);
        Assert.Same(vm.Buttons.Items[1], vm.Selected);
    }

    [Fact]
    public void SwapChoices_ExcludeSelfAndMissing()
    {
        Config.Controller = new ControllerInfoConfig
        {
            ProtocolVersion = 1, Sliders = 3,
            Buttons = new List<IndicatorKind> { IndicatorKind.Led, IndicatorKind.Led },
        };
        var vm = Vm();

        var sliderChoices = vm.SwapChoices(vm.Sliders.Items[1]);
        Assert.Equal(new[] { "Slider 1", "Slider 3" }, sliderChoices.Select(c => c.Label));
        Assert.Same(vm.Sliders.Items[0], sliderChoices[0].Target);

        var buttonChoices = vm.SwapChoices(vm.Buttons.Items[0]);
        Assert.Equal(new[] { "Button 2" }, buttonChoices.Select(c => c.Label));
    }
}
