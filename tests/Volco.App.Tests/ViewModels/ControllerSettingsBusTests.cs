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

/// <summary>Controller-page spec §4.3: the I2C buses a screen uses are rows of Controller settings ›
/// Screens, and a screen button's bus link opens its row.</summary>
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

    [Fact]
    public void BusRowsFollowTheScreens()
    {
        SetUp(screen: true);
        var row = Assert.Single(Settings().Buses);
        Assert.Equal(0, row.Bus);
        Assert.Equal("I2C bus 0 · SDA 25 · SCL 26 · 400 kHz", row.Summary);
        Assert.False(row.IsOpen);
        Assert.False(row.HasProblem);

        // A second screen on another bus adds a row, in bus order.
        Controller.Draft.Buttons[1].Screen = new Screen { Bus = 1 };
        Controller.OnDraftEdited();
        Assert.Equal(new[] { 0, 1 }, Settings().Buses.Select(b => b.Bus));
        Assert.Same(row, Settings().Buses[0]);
    }

    [Fact]
    public void NoScreensNoBusRows()
    {
        SetUp(screen: false);
        Assert.Empty(Settings().Buses);
    }

    [Fact]
    public void BusLinkOpensTheBusRow()
    {
        SetUp(screen: true);
        _settings!.Controls.Select(_settings.Controls.Buttons.Items[0]);
        var button = Assert.IsType<ButtonWiringViewModel>(Hardware.Inspected);
        Assert.Equal("Bus 0, address 3C", button.BusLinkText);

        button.OpenBusCommand.Execute(null);

        var settings = Assert.IsType<ControllerSettingsViewModel>(Hardware.Inspected);
        Assert.True(settings.IsScreensOpen);
        Assert.True(Assert.Single(settings.Buses).IsOpen);
    }

    [Fact]
    public void ABusProblemOpensItsRow()
    {
        SetUp(screen: true);
        var settings = Settings();
        settings.OpenSection = ControllerSettingsViewModel.InputSection;
        var row = Assert.Single(settings.Buses);
        Assert.False(row.IsOpen);

        DraftPins.Set(Controller.Draft, new PinOwner(PinOwnerKind.BusScl, 0), 25);   // SDA = SCL
        Controller.OnDraftEdited();

        Assert.True(row.HasProblem);
        Assert.True(row.IsOpen);
        Assert.True(settings.IsScreensOpen);
        Assert.True(row.Sda.HasError);
        Assert.True(row.Scl.HasError);
    }
}
