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

    /// <summary>The DIY setup saved with button 1's screen on bus 0 (SDA 25, SCL 26).</summary>
    private static readonly byte[] ScreenBlob = ControllerConfigCodec.Encode(WithScreen());

    private static ControllerConfig WithScreen()
    {
        var config = ControllerPresets.ApplyTo(new ControllerConfig(), Diy);
        config.Buttons[0].Screen = new Screen();
        return config;
    }

    /// <summary>Volco's mirror is <paramref name="saved"/> (DIY by default); with <paramref name="greet"/>
    /// the controller says hello with that setup.</summary>
    private void SetUp(bool screen, byte[]? saved = null, bool greet = true)
    {
        saved ??= DiyBlob;
        var config = _rig.Config.Config;
        config.Sliders = Enumerable.Range(0, 5).Select(i => new SliderConfig { Index = i, Name = $"s{i}" }).ToList();
        config.Buttons = Enumerable.Range(0, 3).Select(i => new ButtonConfig { Index = i }).ToList();
        config.ControllerConfigBlob = Convert.ToBase64String(saved);
        _settings = _rig.CreateSettings(_ui);
        _ = _settings.Controls;
        if (!greet) return;
        Greet(saved);
        Assert.True(Controller.CanEdit);
        if (screen)
        {
            Controller.Draft.Buttons[0].Screen = new Screen();   // on bus 0
            Controller.OnDraftEdited();
        }
    }

    private void Greet(byte[] saved)
    {
        var conn = _rig.Connection!;
        conn.SetStatus(TransportStatus.Open);
        var first = saved == ScreenBlob ? IndicatorKind.Screen : IndicatorKind.Led;
        conn.RaiseMessage(new HelloMessage(ProtocolVersion.Current, ChipFamily.Esp32, "dev", ControllerConfigState.Stored,
            ControllerConfigCodec.Crc32(saved), 5, new[] { first, IndicatorKind.Led, IndicatorKind.LedPair }));
        _ui.RunAll();
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
        settings.IsScreensOpen = false;
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
    // ── Find screens (hardware-page spec §6.4) ───────────────────────────────────────────────

    private sealed class Scanner
    {
        public List<(int Sda, int Scl)> Calls { get; } = new();
        public IReadOnlyList<int>? Answer { get; set; } = Array.Empty<int>();

        public Task<IReadOnlyList<int>?> Scan(int sda, int scl, CancellationToken ct)
        {
            Calls.Add((sda, scl));
            return Task.FromResult(Answer);
        }
    }

    private I2cBusesViewModel? _found;

    /// <summary>An I2C buses dialog over the draft whose bus 0 card scans with <paramref name="scanner"/>.</summary>
    private BusWiringViewModel FindRow(Scanner scanner, byte[]? saved = null)
    {
        SetUp(screen: true, saved);
        _found = new I2cBusesViewModel(Controller, Hardware.PinField, () => { }, null, scanner.Scan);
        return _found.Buses.Single(b => b.Bus == 0);
    }

    /// <summary>The draft changed: the dialog follows it, as the Hardware page's does.</summary>
    private void Edited()
    {
        Controller.OnDraftEdited();
        _found!.Refresh();
    }

    [Fact]
    public async Task FindScreens_ListsAddressesAndTheirButtons()
    {
        var scanner = new Scanner { Answer = new[] { 0x3C, 0x3E } };
        var row = FindRow(scanner);
        Controller.Draft.Buttons[0].Screen = new Screen { Bus = 1 };   // off this bus
        Controller.Draft.Buttons[1].Screen = new Screen { Bus = 0, Address = 0x3C };
        Controller.Draft.Buttons[2].Screen = new Screen { Bus = 0, Address = 0x3D };
        Edited();

        await row.FindScreensCommand.ExecuteAsync(null);

        Assert.Equal(new[] { ("0x3C", "Button 2", false), ("0x3E", "", false), ("0x3D", "Button 3", true) },
                     row.Found.Select(f => (f.Address, f.UsedBy, f.IsMissing)));
        Assert.Equal(new[] { "Screen found, used by Button 2", "Screen found, no button uses it", "Button 3's screen didn't answer" },
                     row.Found.Select(f => f.Text));
        Assert.Null(row.FindResult);
    }

    [Fact]
    public async Task FindScreens_UsesUnsavedPins()
    {
        var scanner = new Scanner();
        var row = FindRow(scanner);
        DraftPins.Set(Controller.Draft, new PinOwner(PinOwnerKind.BusSda, 0), 21);
        Edited();

        await row.FindScreensCommand.ExecuteAsync(null);

        Assert.Equal(new[] { (21, 26) }, scanner.Calls);
        Assert.Equal("No screens answered.", row.FindResult);
    }

    [Fact]
    public async Task FindScreens_NoAnswer_SaysSo()
    {
        var row = FindRow(new Scanner { Answer = null });

        await row.FindScreensCommand.ExecuteAsync(null);

        Assert.Equal("The controller didn't answer.", row.FindResult);
        Assert.Empty(row.Found);   // no screen is "Not found": nothing was scanned
    }

    [Fact]
    public async Task FindScreens_PinsHalfOffASavedBus_AsksToSaveFirst()
    {
        // The controller refuses a pair that shares a pin with a saved screen bus without being it.
        var scanner = new Scanner { Answer = new[] { 0x3C } };
        var row = FindRow(scanner, ScreenBlob);
        DraftPins.Set(Controller.Draft, new PinOwner(PinOwnerKind.BusSda, 0), 21);   // SCL stays 26
        Edited();

        await row.FindScreensCommand.ExecuteAsync(null);

        Assert.Empty(scanner.Calls);
        Assert.Equal("Save first to scan these pins.", row.FindResult);
        Assert.Empty(row.Found);
    }

    [Fact]
    public async Task FindScreens_TheSavedBusItself_Scans()
    {
        var scanner = new Scanner { Answer = new[] { 0x3C } };
        var row = FindRow(scanner, ScreenBlob);

        await row.FindScreensCommand.ExecuteAsync(null);

        Assert.Equal(new[] { (25, 26) }, scanner.Calls);
        Assert.Equal("0x3C", Assert.Single(row.Found).Address);
    }

    [Fact]
    public void FindScreens_EnabledOnceTheControllerIsReady()
    {
        SetUp(screen: false, ScreenBlob, greet: false);
        Hardware.OpenBuses();
        var row = Assert.IsType<I2cBusesViewModel>(Hardware.Buses).Buses.Single(b => b.Bus == 0);
        int changed = 0;
        row.FindScreensCommand.CanExecuteChanged += (_, _) => changed++;
        Assert.False(row.FindScreensCommand.CanExecute(null));

        Greet(ScreenBlob);

        Assert.True(changed > 0);
        Assert.True(row.FindScreensCommand.CanExecute(null));
    }

    [Fact]
    public void FindScreens_DisabledWithoutPins()
    {
        var row = FindRow(new Scanner());
        Assert.True(row.FindScreensCommand.CanExecute(null));

        DraftPins.Set(Controller.Draft, new PinOwner(PinOwnerKind.BusScl, 0), null);
        Edited();

        Assert.False(row.FindScreensCommand.CanExecute(null));
    }

    [Fact]
    public void FindScreens_DisabledWithoutAScanner()
    {
        SetUp(screen: true);
        var dialog = new I2cBusesViewModel(Controller, Hardware.PinField, () => { }, null);
        Assert.False(dialog.Buses.Single(b => b.Bus == 0).FindScreensCommand.CanExecute(null));
    }

    [Fact]
    public async Task PinChange_ClearsTheResult()
    {
        var row = FindRow(new Scanner { Answer = new[] { 0x3C } });
        await row.FindScreensCommand.ExecuteAsync(null);
        Assert.NotEmpty(row.Found);

        DraftPins.Set(Controller.Draft, new PinOwner(PinOwnerKind.BusSda, 0), 21);
        Edited();

        Assert.Empty(row.Found);
        Assert.Null(row.FindResult);
    }
}
