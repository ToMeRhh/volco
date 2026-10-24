using Microsoft.Extensions.Time.Testing;
using Volco.App.ViewModels;
using Volco.App.ViewModels.Hardware;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Volco.Core.Runtime;
using Xunit;

namespace Volco.App.Tests.ViewModels;

/// <summary>Hardware-page spec §6.3: the pin field's list, its keys and its problems.</summary>
public sealed class PinFieldViewModelTests : IDisposable
{
    private static readonly ChipInfo Chip = ChipInfo.Esp32;
    private static readonly ControllerPreset DiyPreset = ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId);

    private static ControllerConfig Diy() => ControllerPresets.ApplyTo(new ControllerConfig(), DiyPreset);

    /// <summary>A field over <paramref name="draft"/>'s first slider (or button), recording every set.</summary>
    private sealed class Rig
    {
        public readonly ControllerConfig Draft;
        public readonly List<uint?> Sets = new();
        public readonly PinFieldViewModel Field;
        public uint? Value;

        public Rig(ControllerConfig draft, PinRole role, PinOwner self, uint? value = null)
        {
            Draft = draft;
            Value = value;
            Field = new PinFieldViewModel("Pin", role, self, () => Draft, () => Value, v => { Sets.Add(v); Value = v; }, null);
        }
    }

    private static readonly PinOwner Button1 = new(PinOwnerKind.Button, 0);
    private static readonly PinOwner Slider1 = new(PinOwnerKind.Slider, 0);

    [Fact]
    public void ClosedShowsTheValueOrNotSet()
    {
        var rig = new Rig(new ControllerConfig(), PinRole.ButtonInput, Button1);
        Assert.Equal("Not set", rig.Field.ValueText);
        rig.Value = 5;
        rig.Field.Refresh();
        Assert.Equal("GPIO 5", rig.Field.ValueText);
    }

    [Fact]
    public void OpenListsEveryPinInOrderWithItsNote()
    {
        var rig = new Rig(Diy(), PinRole.ButtonInput, new PinOwner(PinOwnerKind.Button, 3));
        rig.Field.Open();
        Assert.True(rig.Field.IsOpen);
        Assert.Equal(Enumerable.Range(0, 64).Select(p => (uint)p).Where(Chip.Exists), rig.Field.Rows.Select(r => r.Pin));
        var row33 = rig.Field.Rows.Single(r => r.Pin == 33);
        Assert.Equal(("GPIO 33", "Slider 1", false), (row33.Text, row33.Note, row33.IsUnusable));
        Assert.True(rig.Field.Rows.Single(r => r.Pin == 7).IsUnusable);
        Assert.Equal("flash", rig.Field.Rows.Single(r => r.Pin == 7).Note);
    }

    [Fact]
    public void CellsPutEachPinInItsPlace_EightToARow()
    {
        var rig = new Rig(new ControllerConfig(), PinRole.ButtonInput, Button1);
        rig.Field.Open();
        var cells = rig.Field.Cells;
        Assert.Equal(40, cells.Count);   // GPIO 0-39, five rows of eight
        for (uint pin = 0; pin < 40; pin++)
        {
            if (Chip.Exists(pin)) Assert.Equal(pin, Assert.IsType<PinRowViewModel>(cells[(int)pin]).Pin);
            else Assert.IsType<PinGap>(cells[(int)pin]);   // 20, 24, 28-31
        }
        Assert.Equal(rig.Field.Rows, cells.OfType<PinRowViewModel>());
        Assert.Equal("Button 1 pin", rig.Field.Title);
    }

    [Fact]
    public void UnusableRowsCannotBePicked()
    {
        var rig = new Rig(new ControllerConfig(), PinRole.ButtonInput, Button1, value: 4);
        rig.Field.Open();
        rig.Field.Pick(rig.Field.Rows.Single(r => r.Pin == 7));
        Assert.Equal(4u, rig.Value);
        Assert.Empty(rig.Sets);
        Assert.True(rig.Field.IsOpen);
    }

    [Fact]
    public void MoveSkipsUnusablePins_AlongARowAndAColumn()
    {
        var rig = new Rig(new ControllerConfig(), PinRole.ButtonInput, Button1, value: 5);
        rig.Field.Open();
        Assert.Equal(5u, rig.Field.Highlighted!.Pin);
        rig.Field.Move(+1);   // 6–11 are flash
        Assert.Equal(12u, rig.Field.Highlighted!.Pin);
        rig.Field.Move(-1);
        Assert.Equal(5u, rig.Field.Highlighted!.Pin);
        rig.Field.Move(+PinFieldViewModel.Columns);   // the pin under it
        Assert.Equal(13u, rig.Field.Highlighted!.Pin);
        rig.Field.Move(+PinFieldViewModel.Columns);
        Assert.Equal(21u, rig.Field.Highlighted!.Pin);
        rig.Field.Move(+PinFieldViewModel.Columns);   // 29 doesn't exist: on to 37
        Assert.Equal(37u, rig.Field.Highlighted!.Pin);
        rig.Field.Move(+PinFieldViewModel.Columns);   // nothing below: stays
        Assert.Equal(37u, rig.Field.Highlighted!.Pin);
        Assert.True(rig.Field.Rows.Single(r => r.Pin == 37).IsHighlighted);
        Assert.False(rig.Field.Rows.Single(r => r.Pin == 5).IsHighlighted);
    }

    [Fact]
    public void EscapeRestoresTheOldValue()
    {
        var rig = new Rig(new ControllerConfig(), PinRole.ButtonInput, Button1, value: 4);
        rig.Field.Open();
        rig.Field.Move(+3);
        rig.Field.Cancel();
        Assert.Equal(4u, rig.Value);
        Assert.Empty(rig.Sets);
        Assert.False(rig.Field.IsOpen);
        Assert.Equal("GPIO 4", rig.Field.ValueText);
    }

    [Fact]
    public void EnterPicksTheHighlightedPin()
    {
        var rig = new Rig(new ControllerConfig(), PinRole.SliderInput, Slider1);
        rig.Field.Open();
        Assert.Equal(0u, rig.Field.Highlighted!.Pin);   // nothing set: the first pin a slider can use
        rig.Field.Move(+1);   // 1 is USB serial
        Assert.Equal(2u, rig.Field.Highlighted!.Pin);
        rig.Field.Commit();
        Assert.Equal(new uint?[] { 2 }, rig.Sets);
        Assert.False(rig.Field.IsOpen);
        Assert.Equal("GPIO 2", rig.Field.ValueText);
    }

    [Fact]
    public void NotEditableDoesNotOpen()
    {
        var rig = new Rig(new ControllerConfig(), PinRole.ButtonInput, Button1);
        rig.Field.IsEditable = false;
        rig.Field.Open();
        Assert.False(rig.Field.IsOpen);
    }

    // ── Through the Hardware view model (spec §6.3 problems, §6.5) ──────────────────────────

    private readonly TestRig _rig = new();
    private readonly RecordingDispatcher _ui = new();
    private SettingsViewModel? _settings;

    public void Dispose()
    {
        _settings?.Dispose();
        _rig.Dispose();
    }

    private HardwareViewModel InSyncHardware()
    {
        var blob = ControllerConfigCodec.Encode(Diy());
        var config = _rig.Config.Config;
        config.Sliders = Enumerable.Range(0, 5).Select(i => new SliderConfig { Index = i }).ToList();
        config.Buttons = Enumerable.Range(0, 3).Select(i => new ButtonConfig { Index = i }).ToList();
        config.ControllerConfigBlob = Convert.ToBase64String(blob);
        _settings = _rig.CreateSettings(_ui);
        _ = _settings.Controls;
        var conn = _rig.Connection!;
        conn.SetStatus(TransportStatus.Open);
        conn.RaiseMessage(new HelloMessage(ProtocolVersion.Current, ChipFamily.Esp32, "dev", ControllerConfigState.Stored,
                                           ControllerConfigCodec.Crc32(blob), 5,
                                           new[] { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair }));
        _ui.RunAll();
        Assert.True(_settings.Controller.CanEdit);
        return _settings.Hardware;
    }

    [Fact]
    public void PickingATakenPinShowsTheProblemAndSwapFixesIt()
    {
        var hardware = InSyncHardware();
        var draft = _settings!.Controller.Draft;
        var slider5 = hardware.PinField(new PinOwner(PinOwnerKind.Slider, 4));
        var slider1 = hardware.PinField(Slider1);
        Assert.Equal("Pin", slider5.Label);
        var before = draft.Sliders[4].Pin;

        slider5.Open();
        slider5.Pick(slider5.Rows.Single(r => r.Pin == 33));

        Assert.Equal(33u, draft.Sliders[4].Pin);
        Assert.Equal("Slider 1 also uses GPIO 33.", slider5.Error);
        Assert.Equal("Swap with Slider 1", slider5.FixLabel);
        Assert.Equal("Slider 5 also uses GPIO 33.", slider1.Error);   // the other end is marked too

        slider5.FixCommand.Execute(null);

        Assert.Equal(33u, draft.Sliders[4].Pin);
        Assert.Equal(before, draft.Sliders[0].Pin);
        Assert.Null(slider5.Error);
        Assert.Null(slider5.FixLabel);
        Assert.Null(slider1.Error);
        Assert.Equal($"GPIO {before}", slider1.ValueText);
    }

    [Fact]
    public void FieldsAreDisabledWhileTheDraftCantBeEdited()
    {
        var hardware = InSyncHardware();
        var light = hardware.PinField(new PinOwner(PinOwnerKind.LightA, 2));
        Assert.Equal("Light A pin", light.Label);
        Assert.Equal("GPIO 18", light.ValueText);
        Assert.True(light.IsEditable);
        light.Open();
        light.Pick(light.Rows.Single(r => r.Pin == 23));
        Assert.Equal("GPIO 23", light.ValueText);
        Assert.Equal(23u, _settings!.Controller.Draft.Buttons[2].LedPair.PinA);
        _settings.Controller.SaveCommand.Execute(null);   // the draft can't change while a save runs
        Assert.False(_settings.Controller.CanEdit);
        Assert.False(light.IsEditable);
        light.Open();
        Assert.False(light.IsOpen);
    }

    /// <summary>A field over <paramref name="role"/> whose watch func records the roles it was asked for and
    /// how many of the watches it handed out were disposed.</summary>
    private sealed class WatchRig
    {
        public readonly List<PinWatchRole> Started = new();
        public int Disposed;
        public readonly PinFieldViewModel Field;
        public readonly FakeTimeProvider Time = new();

        public WatchRig(PinRole role, PinOwner self, bool withWatch = true)
        {
            var draft = Diy();
            Field = new PinFieldViewModel("Pin", role, self, () => draft, () => 33u, _ => { }, null,
                                          withWatch ? r => { Started.Add(r); return new Stop(this); } : null, Time);
        }

        private sealed class Stop(WatchRig rig) : IDisposable
        {
            public void Dispose() => rig.Disposed++;
        }
    }

    [Fact]
    public void Open_SliderPin_StartsAnalogWatch_CloseStopsIt()
    {
        var rig = new WatchRig(PinRole.SliderInput, Slider1);
        rig.Field.Open();
        Assert.Equal([PinWatchRole.Analog], rig.Started);
        Assert.Equal(0, rig.Disposed);
        rig.Field.Cancel();
        Assert.Equal(1, rig.Disposed);

        var buttons = new WatchRig(PinRole.ButtonInput, Button1);
        buttons.Field.Open();
        Assert.Equal([PinWatchRole.Button], buttons.Started);
    }

    [Fact]
    public void NotEditable_StopsTheWatch()
    {
        var rig = new WatchRig(PinRole.SliderInput, Slider1);
        rig.Field.Open();
        rig.Field.IsEditable = false;
        Assert.False(rig.Field.IsOpen);
        Assert.Equal(1, rig.Disposed);
    }

    [Fact]
    public void LightPin_StartsNoWatch()
    {
        var rig = new WatchRig(PinRole.LightOutput, new PinOwner(PinOwnerKind.Light, 0));
        rig.Field.Open();
        Assert.Empty(rig.Started);
    }

    [Fact]
    public void Activity_MarksThePinWithoutSelectingIt_NowThenBefore()
    {
        var rig = new WatchRig(PinRole.SliderInput, Slider1);
        rig.Field.Open();
        Assert.Equal("Listening for slider movements…", rig.Field.WatchHint);
        var selected = rig.Field.Highlighted;
        rig.Field.OnPinActivity(32);
        var row = rig.Field.Rows.Single(r => r.Pin == 32);
        Assert.Equal(PinActivityState.Now, row.Activity);
        Assert.True(row.IsMovingNow);
        Assert.Same(selected, rig.Field.Highlighted);   // marked, not selected
        Assert.DoesNotContain(rig.Field.Rows, r => r.IsHighlighted && r.Pin == 32);

        rig.Time.Advance(TimeSpan.FromMilliseconds(600));
        rig.Field.OnPinActivity(32);                      // still moving: the hold starts again
        rig.Time.Advance(TimeSpan.FromMilliseconds(600));
        Assert.True(row.IsMovingNow);
        rig.Time.Advance(PinFieldViewModel.ActivityHold);
        Assert.Equal(PinActivityState.Before, row.Activity);
        Assert.True(row.HasMoved);
        Assert.False(row.IsMovingNow);

        rig.Field.OnPinActivity(34);                      // another one: both stay marked
        Assert.True(rig.Field.Rows.Single(r => r.Pin == 34).IsMovingNow);
        Assert.True(row.HasMoved);
        rig.Field.OnPinActivity(32);                      // moved again: now once more
        Assert.True(row.IsMovingNow);

        rig.Field.OnPinActivity(200);                     // a pin the chip doesn't list
        Assert.Equal(2, rig.Field.Rows.Count(r => r.Activity != PinActivityState.None));

        var buttons = new WatchRig(PinRole.ButtonInput, Button1);
        buttons.Field.Open();
        Assert.Equal("Listening for button presses…", buttons.Field.WatchHint);
        buttons.Field.OnPinActivity(4);
        Assert.True(buttons.Field.Rows.Single(r => r.Pin == 4).IsMovingNow);
    }

    [Fact]
    public void Activity_SurvivesARefresh_AndGoesWithTheDialog()
    {
        var rig = new WatchRig(PinRole.SliderInput, Slider1);
        rig.Field.Open();
        rig.Field.OnPinActivity(32);
        rig.Time.Advance(PinFieldViewModel.ActivityHold);
        rig.Field.OnPinActivity(34);
        rig.Field.Refresh();
        Assert.True(rig.Field.Rows.Single(r => r.Pin == 32).HasMoved);
        Assert.True(rig.Field.Rows.Single(r => r.Pin == 34).IsMovingNow);

        rig.Field.Cancel();
        rig.Time.Advance(PinFieldViewModel.ActivityHold);   // the hold of a closed dialog does nothing
        rig.Field.Open();
        Assert.All(rig.Field.Rows, r => Assert.Equal(PinActivityState.None, r.Activity));
    }

    [Fact]
    public void Activity_WhenClosed_IsIgnored()
    {
        var rig = new WatchRig(PinRole.SliderInput, Slider1);
        rig.Field.OnPinActivity(33);
        rig.Field.Open();
        rig.Field.Cancel();
        rig.Field.OnPinActivity(33);
        Assert.Empty(rig.Field.Rows);
        Assert.Null(rig.Field.Highlighted);
        Assert.Null(rig.Field.WatchHint);
    }

    [Fact]
    public void NoWatchFunc_NothingLights()
    {
        var rig = new WatchRig(PinRole.SliderInput, Slider1, withWatch: false);
        rig.Field.Open();
        rig.Field.OnPinActivity(32);
        Assert.DoesNotContain(rig.Field.Rows, r => r.Activity != PinActivityState.None);
        Assert.Null(rig.Field.WatchHint);
    }

    // ── Opened by + Add ─────────────────────────────────────────────────────────────────────

    /// <summary>A watch rig opened to add, counting the cancels.</summary>
    private static (WatchRig Rig, Func<int> Cancels) Adding(PinRole role, PinOwner self, bool withWatch = true)
    {
        var rig = new WatchRig(role, self, withWatch);
        var cancels = 0;
        rig.Field.OpenToAdd(() => cancels++);
        return (rig, () => cancels);
    }

    [Fact]
    public void OpenToAdd_ListsNothing_UntilAPinIsHeard_ThenSelectsTheFirst()
    {
        var (rig, _) = Adding(PinRole.SliderInput, new PinOwner(PinOwnerKind.Slider, 5));
        var f = rig.Field;
        Assert.True(f is { IsOpen: true, IsAdding: true, IsListView: true, IsGridView: false, IsWaiting: true });
        Assert.Empty(f.Heard);
        Assert.Null(f.Highlighted);
        Assert.False(f.CanConfirm);
        Assert.Equal("Add Slider 6", f.Title);

        f.OnPinActivity(34);
        f.OnPinActivity(32);
        f.OnPinActivity(34);   // heard again: keeps its place
        Assert.Equal([34u, 32u], f.Heard.Select(r => r.Pin));
        Assert.False(f.IsWaiting);
        Assert.Equal(34u, f.Highlighted!.Pin);
        Assert.True(f.CanConfirm);
        Assert.True(f.Heard[0].IsMovingNow);
    }

    [Fact]
    public void OpenToAdd_AClickSelects_AddSets_AndNoCancel()
    {
        var (rig, cancels) = Adding(PinRole.ButtonInput, new PinOwner(PinOwnerKind.Button, 3));
        var f = rig.Field;
        f.OnPinActivity(4);
        f.OnPinActivity(5);
        f.Pick(f.Heard[1]);
        Assert.True(f.IsOpen);   // a click only selects
        Assert.Equal(5u, f.Highlighted!.Pin);
        f.Move(-1);
        Assert.Equal(4u, f.Highlighted!.Pin);
        f.Commit();              // Enter is Add
        Assert.False(f.IsOpen);
        Assert.False(f.IsAdding);
        Assert.Equal(0, cancels());
        Assert.Equal("Button 4 pin", f.Title);
    }

    [Fact]
    public void OpenToAdd_CancelRunsTheCancelOnce()
    {
        var (rig, cancels) = Adding(PinRole.SliderInput, Slider1);
        rig.Field.Cancel();
        rig.Field.Cancel();
        Assert.Equal(1, cancels());
        Assert.Equal(1, rig.Disposed);
        rig.Field.Open();        // opened from the field later: not adding
        Assert.False(rig.Field.IsAdding);
        rig.Field.Cancel();
        Assert.Equal(1, cancels());
    }

    [Fact]
    public void OpenToAdd_NotEditable_Cancels()
    {
        var (rig, cancels) = Adding(PinRole.SliderInput, Slider1);
        rig.Field.IsEditable = false;
        Assert.False(rig.Field.IsOpen);
        Assert.Equal(1, cancels());
    }

    [Fact]
    public void ChooseManually_ShowsTheGrid_KeepingTheSelectionAndTheMarks()
    {
        var (rig, _) = Adding(PinRole.SliderInput, Slider1);
        var f = rig.Field;
        f.OnPinActivity(32);
        f.ChooseManually();
        Assert.True(f is { IsGridView: true, IsListView: false, IsAdding: true });
        Assert.Equal(32u, f.Highlighted!.Pin);
        Assert.True(f.Rows.Single(r => r.Pin == 32).IsMovingNow);
        f.Pick(f.Rows.Single(r => r.Pin == 35));
        Assert.True(f.IsOpen);
        Assert.Equal(35u, f.Highlighted!.Pin);
        f.Move(-1);              // the grid's moves again: by pin number
        Assert.Equal(34u, f.Highlighted!.Pin);
    }

    [Fact]
    public void OpenToAdd_WithoutAWatch_OpensOnTheGrid()
    {
        var (rig, _) = Adding(PinRole.SliderInput, Slider1, withWatch: false);
        Assert.True(rig.Field is { IsGridView: true, IsAdding: true, HasWatchHint: false });
        Assert.Null(rig.Field.Highlighted);
    }

    [Fact]
    public void DraftPinsReadAndWriteEveryUse()
    {
        var d = Diy();
        Assert.Equal(12u, DraftPins.Get(d, new PinOwner(PinOwnerKind.Light, 0)));
        Assert.Null(DraftPins.Get(d, new PinOwner(PinOwnerKind.Light, 2)));   // button 3 has a pair
        Assert.Equal(19u, DraftPins.Get(d, new PinOwner(PinOwnerKind.LightB, 2)));
        Assert.Equal(13u, DraftPins.Get(d, new PinOwner(PinOwnerKind.BusScl, 1)));
        Assert.Null(DraftPins.Get(d, new PinOwner(PinOwnerKind.BusSda, 3)));

        DraftPins.Set(d, new PinOwner(PinOwnerKind.BusSda, 3), 23);
        Assert.Equal(23u, d.I2C3.Sda);
        DraftPins.Set(d, new PinOwner(PinOwnerKind.Button, 1), null);
        Assert.False(d.Buttons[1].HasPin);
        DraftPins.Swap(d, Slider1, new PinOwner(PinOwnerKind.LightA, 2));
        Assert.Equal((18u, 33u), (d.Sliders[0].Pin, d.Buttons[2].LedPair.PinA));
    }
}
