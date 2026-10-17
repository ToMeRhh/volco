using Microsoft.Extensions.Time.Testing;
using Volco.App.ViewModels;
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Input;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.App.Tests.ViewModels;

/// <summary>Spec docs/design/controls-page.md §4.1, §4.3, §4.5 and §5.2 (Clear): the Controls
/// page's drawing model, selection and touch-to-select.</summary>
public sealed class ControlsViewModelTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly TestRig _rig;
    private readonly UndoToastViewModel _toast = new();

    public ControlsViewModelTests()
    {
        var speakers = new AudioDevice("dev-1", "Speakers", AudioFlow.Render, true);
        var engine = new FakeAudioEngine { Devices = { speakers }, DefaultRender = speakers };
        engine.DeviceStates["dev-1"] = (0.5f, true);   // muted
        _rig = new TestRig(engine);
    }

    public void Dispose() => _rig.Dispose();

    private AppConfig Config => _rig.Config.Config;

    private ControlsViewModel Vm()
    {
        var sliders = new SlidersViewModel(_rig.Config, _rig.Engine, _toast);
        var buttons = new ButtonsViewModel(_rig.Config, _rig.Engine, _rig.Runtime, _rig.Runtime.Execute, _toast);
        return new ControlsViewModel(sliders, buttons, _rig.Config, _toast, _time);
    }

    private void Hardware(int sliders, int buttons)
        => Config.Controller = new ControllerInfoConfig
        {
            ProtocolVersion = 1, Sliders = sliders,
            Buttons = Enumerable.Repeat(IndicatorKind.Led, buttons).ToList(),
        };

    // ── Drawing ───────────────────────────────────────────────────────────────

    [Fact]
    public void Select_Default_IsFirstSliderElseFirstButton()
    {
        var vm = Vm();
        Assert.Same(vm.Sliders.Items[0], vm.Selected);

        Config.Sliders.Clear();
        var noSliders = Vm();
        Assert.Same(noSliders.Buttons.Items[0], noSliders.Selected);
    }

    [Fact]
    public void Hello_MoreButtonsThanConfig_AddsNotSet()
    {
        var vm = Vm();
        Assert.True(vm.ShowConnectLine);
        Assert.Equal(3, vm.Buttons.Items.Count);

        Hardware(sliders: 5, buttons: 5);
        vm.OnHello();

        Assert.False(vm.ShowConnectLine);
        Assert.Equal(5, vm.Buttons.Items.Count);
        Assert.Equal(5, Config.Buttons.Count);
        Assert.Equal("Not set", vm.Buttons.Items[4].MainLabel);
        Assert.Null(vm.Buttons.Items[4].MainGesture);
        Assert.All(vm.Buttons.Items, b => Assert.False(b.IsMissing));
    }

    [Fact]
    public void ConfigBeyondHello_IsMissing()
    {
        Hardware(sliders: 3, buttons: 2);
        var vm = Vm();

        Assert.Equal(5, vm.Sliders.Items.Count);
        Assert.Equal(new[] { false, false, false, true, true }, vm.Sliders.Items.Select(s => s.IsMissing).ToArray());
        Assert.Equal(new[] { false, false, true }, vm.Buttons.Items.Select(b => b.IsMissing).ToArray());
    }

    [Fact]
    public void Hello_Shrinks_SelectedMissingMovesToFirstSlider()
    {
        var vm = Vm();
        var third = vm.Buttons.Items[2];
        vm.Select(third);

        // A Hello with fewer buttons than the config: drawn missing, still selected.
        Hardware(sliders: 5, buttons: 2);
        vm.OnHello();
        Assert.True(third.IsMissing);
        Assert.Same(third, vm.Selected);

        // Removed from the config: the selection moves to slider 1.
        vm.Buttons.RemoveLast();
        Assert.Equal("Button 3 removed", _toast.Message);   // numbered from 1, as in the inspector
        vm.OnHello();
        Assert.Same(vm.Sliders.Items[0], vm.Selected);
    }

    [Fact]
    public void IsDense_NineButtons()
    {
        var vm = Vm();
        Assert.False(vm.IsDense);

        Hardware(sliders: 5, buttons: 9);
        vm.OnHello();
        Assert.True(vm.IsDense);
    }

    // ── Touch-to-select ───────────────────────────────────────────────────────

    [Fact]
    public void ButtonActivity_SelectsAndPulses()
    {
        var vm = Vm();
        var button = vm.Buttons.Items[1];
        var pulses = 0;
        button.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ButtonItemViewModel.IsActive) && button.IsActive) pulses++; };

        vm.OnButtonActivity(1);

        Assert.Same(button, vm.Selected);
        Assert.Equal(1, pulses);
    }

    [Fact]
    public void ButtonActivity_WhileDialogOpen_KeepsSelection()
    {
        var vm = Vm();
        var selected = vm.Selected;
        vm.OpenDialog(new UndoToastViewModel());
        Assert.True(vm.HasDialog);

        vm.OnButtonActivity(1);
        Assert.Same(selected, vm.Selected);
        Assert.True(vm.Buttons.Items[1].IsActive);   // the pulse still shows

        vm.CloseDialog();
        Assert.False(vm.HasDialog);
        vm.OnButtonActivity(1);
        Assert.Same(vm.Buttons.Items[1], vm.Selected);
    }

    [Fact]
    public void SliderLevel_SmallChange_DoesNotSelect()
    {
        var vm = Vm();
        vm.OnSliderLevel(2, 0.50f);
        vm.OnSliderLevel(2, 0.51f);
        Assert.Same(vm.Sliders.Items[0], vm.Selected);
        Assert.Equal(0.51, vm.Sliders.Items[2].LiveLevel, 3);
    }

    [Fact]
    public void SliderLevel_TwoPercent_Selects()
    {
        var vm = Vm();
        vm.OnSliderLevel(2, 0.50f);
        vm.OnSliderLevel(2, 0.52f);
        Assert.Same(vm.Sliders.Items[2], vm.Selected);
    }

    [Fact]
    public void SliderLevel_WithinOneSecondOfHello_DoesNotSelect()
    {
        Hardware(sliders: 5, buttons: 3);
        var vm = Vm();
        vm.OnSliderLevel(2, 0.50f);

        vm.OnHello();
        _time.Advance(TimeSpan.FromMilliseconds(900));
        vm.OnSliderLevel(2, 0.80f);   // the Hello's sync
        Assert.Same(vm.Sliders.Items[0], vm.Selected);

        _time.Advance(TimeSpan.FromMilliseconds(200));
        vm.OnSliderLevel(2, 0.81f);   // measured from the synced level
        Assert.Same(vm.Sliders.Items[0], vm.Selected);
        vm.OnSliderLevel(2, 0.85f);
        Assert.Same(vm.Sliders.Items[2], vm.Selected);
    }

    [Fact]
    public void SliderLevel_WhileMenuOpen_DoesNotSelect()
    {
        var vm = Vm();
        vm.OnSliderLevel(2, 0.50f);
        vm.IsMenuOpen = true;
        vm.OnSliderLevel(2, 0.70f);
        Assert.Same(vm.Sliders.Items[0], vm.Selected);
    }

    // ── Button tiles ──────────────────────────────────────────────────────────

    [Fact]
    public void ButtonItem_MainLabelAndSecondary()
    {
        var binding = Config.Buttons[0].Default();
        binding.DoublePress.Action = ButtonActionType.MuteCurrentInput;
        binding.LongPress.Action = ButtonActionType.NextPreset;
        var item = Vm().Buttons.Items[0];

        Assert.Equal("Mute mic", item.MainLabel);
        Assert.Equal(PressType.Double, item.MainGesture);
        Assert.Equal(new[] { new GestureLine(PressType.Long, "Next mode", "NextPreset") }, item.Secondary.ToArray());
    }

    [Fact]
    public void ClearButton_EmptiesEditedPresetOnly_UndoRestores()
    {
        Config.Presets.Add(new PresetConfig { Id = "gaming", Name = "Gaming" });
        Config.Buttons[1].Default().SinglePress.Action = ButtonActionType.MuteCurrentOutput;
        Config.Buttons[1].GetOrAddBinding("gaming").SinglePress.Action = ButtonActionType.MuteCurrentInput;
        var vm = Vm();
        vm.Buttons.EditingPresetId = "gaming";
        var item = vm.Buttons.Items[1];
        Assert.True(item.HasEditedActions);

        vm.ClearButton(item);

        Assert.True(Config.Buttons[1].BindingFor("gaming").IsEmpty);
        Assert.Equal(ButtonActionType.MuteCurrentOutput, Config.Buttons[1].Default().SinglePress.Action);
        Assert.Equal("Not set", item.MainLabel);
        Assert.False(item.HasEditedActions);
        Assert.Equal("Cleared button 2 in Gaming", _toast.Message);

        _toast.UndoCommand.Execute(null);

        Assert.Equal(ButtonActionType.MuteCurrentInput, Config.Buttons[1].BindingFor("gaming").SinglePress.Action);
        Assert.Equal("Mute mic", item.MainLabel);
        Assert.Equal(ButtonActionType.MuteCurrentInput, item.Press.SelectedChoice.Type);
    }

    [Fact]
    public void Preview_ShowsEditedPreset()
    {
        Hardware(sliders: 5, buttons: 3);
        Config.Presets.Add(new PresetConfig { Id = "gaming", Name = "Gaming" });
        // The output is muted: a mute action lights the LED, an empty binding doesn't.
        Config.Buttons[0].GetOrAddBinding("gaming").SinglePress.Action = ButtonActionType.MuteCurrentOutput;
        var vm = Vm();
        var item = vm.Buttons.Items[0];
        Assert.Equal(AppConfig.DefaultPresetId, vm.Buttons.LivePresetId);
        Assert.False(item.LedOn);

        vm.Buttons.EditingPresetId = "gaming";
        Assert.True(item.LedOn);
        Assert.Equal(AppConfig.DefaultPresetId, vm.Buttons.LivePresetId);
    }

    [Fact]
    public void SliderItem_LabelIsNameElseTargetElseNotSet()
    {
        var item = Vm().Sliders.Items[0];
        Assert.Equal("Not set", item.Label);

        item.IsOtherApps = true;
        Assert.Equal("All other apps", item.Label);

        // The default output is "Main output" on the tile and the Controls row, as in the dialog.
        item.IsDevice = true;
        Assert.Equal("Main output", item.Label);
        Assert.Equal("Main output", item.TargetLabel);

        item.Name = "Games";
        Assert.Equal("Games", item.Label);
        Assert.Equal("Games", Config.Sliders[0].Name);
    }
}
