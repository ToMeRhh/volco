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
    public void QueryFiltersByNumberAndNote()
    {
        var rig = new Rig(new ControllerConfig(), PinRole.ButtonInput, Button1);
        rig.Field.Open();
        rig.Field.Query = "3";
        var expected = Enumerable.Range(0, 64).Select(p => (uint)p).Where(p => Chip.Exists(p) && p.ToString().Contains('3'));
        Assert.Equal(expected, rig.Field.Rows.Select(r => r.Pin));
        Assert.Contains(3u, rig.Field.Rows.Select(r => r.Pin));
        Assert.Contains(13u, rig.Field.Rows.Select(r => r.Pin));
        Assert.Contains(39u, rig.Field.Rows.Select(r => r.Pin));
        Assert.Equal(new[] { 5 }, rig.Field.Rows.Single(r => r.Pin == 3).Matches);

        var diy = new Rig(Diy(), PinRole.ButtonInput, new PinOwner(PinOwnerKind.Button, 3));
        diy.Field.Open();
        diy.Field.Query = "slider";
        Assert.NotEmpty(diy.Field.Rows);
        Assert.All(diy.Field.Rows, r => Assert.StartsWith("Slider", r.Note));
        Assert.Equal(new uint[] { 32, 33, 34, 35, 36 }, diy.Field.Rows.Select(r => r.Pin));

        diy.Field.Query = "";
        Assert.Equal(Enumerable.Range(0, 64).Count(p => Chip.Exists((uint)p)), diy.Field.Rows.Count);
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
    public void MoveSkipsUnusableRows()
    {
        var rig = new Rig(new ControllerConfig(), PinRole.ButtonInput, Button1, value: 5);
        rig.Field.Open();
        Assert.Equal(5u, rig.Field.Highlighted!.Pin);
        rig.Field.Move(+1);   // 6–11 are flash
        Assert.Equal(12u, rig.Field.Highlighted!.Pin);
        rig.Field.Move(-1);
        Assert.Equal(5u, rig.Field.Highlighted!.Pin);
        Assert.True(rig.Field.Rows.Single(r => r.Pin == 5).IsHighlighted);
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
    public void EnterPicksTheHighlightedRow()
    {
        var rig = new Rig(new ControllerConfig(), PinRole.SliderInput, Slider1);
        rig.Field.Open();
        rig.Field.Query = "36";
        Assert.Equal(36u, rig.Field.Highlighted!.Pin);
        rig.Field.Commit();
        Assert.Equal(new uint?[] { 36 }, rig.Sets);
        Assert.False(rig.Field.IsOpen);
        Assert.Equal("GPIO 36", rig.Field.ValueText);
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
