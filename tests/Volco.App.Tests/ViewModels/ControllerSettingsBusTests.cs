using Volco.App.ViewModels;
using Volco.App.ViewModels.Hardware;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Volco.Core.Runtime;
using Volco.Core.Tests.Runtime;
using Xunit;

namespace Volco.App.Tests.ViewModels;

/// <summary>Controller-page spec §4.3: the I2C buses are edited in their own dialog, opened from
/// Controller settings › Screens (a summary and Edit) and from a screen button's bus link.</summary>
public sealed class ControllerSettingsBusTests : IDisposable
{
    private readonly TestRig _rig = new();
    private readonly RecordingDispatcher _ui = new();
    private SettingsViewModel? _settings;

    public void Dispose()
    {
        _settings?.Dispose();
        _rig.Dispose();
    }

    private static readonly ControllerPreset Diy = ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId);
    private static readonly byte[] DiyBlob = ControllerConfigCodec.Encode(ControllerPresets.ApplyTo(new ControllerConfig(), Diy));

    private ControllerViewModel Controller => _settings!.Controller;
    private HardwareViewModel Hardware => _settings!.Hardware;

    private void SetUp(bool screen)
    {
        var config = _rig.Config.Config;
        config.Sliders = Enumerable.Range(0, 5).Select(i => new SliderConfig { Index = i, Name = $"s{i}" }).ToList();
        config.Buttons = Enumerable.Range(0, 3).Select(i => new ButtonConfig { Index = i }).ToList();
        config.ControllerConfigBlob = Convert.ToBase64String(DiyBlob);
        _settings = _rig.CreateSettings(_ui);
        _ = _settings.Controls;
        var conn = _rig.Connection!;
        conn.SetStatus(TransportStatus.Open);
        conn.RaiseMessage(new HelloMessage(ProtocolVersion.Current, ChipFamily.Esp32, "dev", ControllerConfigState.Stored,
            ControllerConfigCodec.Crc32(DiyBlob), 5, new[] { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair }));
        _ui.RunAll();
        Assert.True(Controller.CanEdit);
        if (screen)
        {
            Controller.Draft.Buttons[0].Screen = new Screen();   // on bus 0
            Controller.OnDraftEdited();
        }
    }

    private ControllerSettingsViewModel Settings()
    {
        Hardware.SelectControllerSettings();
        return Assert.IsType<ControllerSettingsViewModel>(Hardware.Inspected);
    }

    private I2cBusesViewModel Dialog(int? bus = null)
    {
        Hardware.OpenBuses(bus);
        return Assert.IsType<I2cBusesViewModel>(Hardware.Buses);
    }

    /// <summary>The DIY preset defines buses 0 and 1; the dialog lists them whether or not a screen
    /// uses them.</summary>
    [Fact]
    public void TheDialogListsTheDraftsBusesAndTheUsedOnes()
    {
        SetUp(screen: true);
        var dialog = Dialog();
        Assert.Equal(new[] { 0, 1 }, dialog.Buses.Select(b => b.Bus));
        var row = dialog.Buses[0];
        Assert.Equal("Bus 0", row.Title);
        Assert.Equal("Hardware I2C", row.KindText);
        Assert.Equal("Used by Button 1", row.UsedByText);
        Assert.Equal("GPIO 25", row.Sda.ValueText);
        Assert.False(row.HasProblem);
        Assert.Equal("No screen uses it", dialog.Buses[1].UsedByText);

        // A screen on a bus the draft doesn't have adds that bus, in bus order, with a problem.
        Controller.Draft.Buttons[1].Screen = new Screen { Bus = 2 };
        Controller.OnDraftEdited();
        Assert.Equal(new[] { 0, 1, 2 }, dialog.Buses.Select(b => b.Bus));
        Assert.Same(row, dialog.Buses[0]);
        Assert.Equal("Software I2C", dialog.Buses[2].KindText);
        Assert.True(dialog.Buses[2].HasProblem);
        Assert.Equal("Bus 2 needs attention", Settings().BusesSummary);
        Assert.True(Settings().BusesHaveProblem);
    }

    [Fact]
    public void ABusAScreenUsesCantBeRemoved()
    {
        SetUp(screen: true);
        var row = Dialog().Buses.Single(b => b.Bus == 0);
        Assert.False(row.CanRemove);
        Assert.False(row.RemoveCommand.CanExecute(null));
        Assert.Equal("Used by Button 1: put their screens on another bus first", row.RemoveTooltip);

        Controller.Draft.Buttons[0].ClearIndicator();   // the screen goes: the bus stays, unused
        Controller.OnDraftEdited();
        Assert.Equal("No screen uses it", row.UsedByText);
        Assert.True(row.CanRemove);
        Assert.Null(row.RemoveTooltip);
    }

    [Fact]
    public void RemoveAndAdd()
    {
        SetUp(screen: false);
        var dialog = Dialog();
        Assert.Equal("Buses 0 and 1", Settings().BusesSummary);
        Assert.Equal("Add bus 2", dialog.AddText);

        dialog.Buses[0].RemoveCommand.Execute(null);
        Assert.Null(ControllerDraft.Bus(Controller.Draft, 0));
        Assert.Equal("Bus 1", Settings().BusesSummary);
        dialog.Buses[0].RemoveCommand.Execute(null);
        Assert.True(dialog.HasNoBuses);
        Assert.Equal("None", Settings().BusesSummary);
        Assert.True(Controller.IsDirty);

        // Add takes the lowest free number, its pins not set yet
        Assert.Equal("Add bus 0", dialog.AddText);
        dialog.AddCommand.Execute(null);
        dialog.AddCommand.Execute(null);
        dialog.AddCommand.Execute(null);
        Assert.Equal(new[] { 0, 1, 2 }, dialog.Buses.Select(b => b.Bus));
        Assert.False(ControllerDraft.Bus(Controller.Draft, 1)!.HasSda);
        Assert.Equal("Buses 0, 1 and 2", Settings().BusesSummary);
        dialog.AddCommand.Execute(null);
        Assert.False(dialog.CanAdd);
        Assert.False(dialog.AddCommand.CanExecute(null));
    }

    [Fact]
    public void BusLinkOpensTheDialogAtItsBus()
    {
        SetUp(screen: true);
        _settings!.Controls.Select(_settings.Controls.Buttons.Items[0]);
        var button = Assert.IsType<ButtonWiringViewModel>(Hardware.Inspected);
        Assert.Equal("Bus 0 wiring", button.BusLinkLabel);
        Assert.Equal("SDA 25 · SCL 26 · 400 kHz", button.BusLinkText);

        button.OpenBusCommand.Execute(null);

        var dialog = Assert.IsType<I2cBusesViewModel>(Hardware.Buses);
        Assert.Equal(0, dialog.FocusedBus);
        Assert.Equal(0, dialog.Buses.Single(b => b.IsFocused).Bus);
        Assert.Same(button, Hardware.Inspected);   // the button stays in the inspector

        dialog.DoneCommand.Execute(null);
        Assert.Null(Hardware.Buses);
    }

    [Fact]
    public void ABusProblemOpensScreensAndShowsInTheDialog()
    {
        SetUp(screen: true);
        var settings = Settings();
        settings.OpenSection = ControllerSettingsViewModel.InputSection;
        var row = Dialog().Buses.Single(b => b.Bus == 0);

        DraftPins.Set(Controller.Draft, new PinOwner(PinOwnerKind.BusScl, 0), 25);   // SDA = SCL
        Controller.OnDraftEdited();

        Assert.True(row.HasProblem);
        Assert.True(settings.IsScreensOpen);
        Assert.True(settings.BusesHaveProblem);
        Assert.Equal("Bus 0 needs attention", settings.BusesSummary);
        Assert.True(row.Sda.HasError);
        Assert.True(row.Scl.HasError);
    }
}
