using System.Diagnostics;
using Volco.App.ViewModels;
using Volco.App.ViewModels.Hardware;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Volco.Core.Firmware;
using Volco.Core.Runtime;
using Volco.Core.Tests.Firmware;
using Xunit;

namespace Volco.App.Tests.ViewModels;

/// <summary>Hardware-page spec §4: the device card's states (§4.1, first match wins) and its ⋯ menu (§4.2).</summary>
public sealed class DeviceCardTests : IDisposable
{
    private static CardInputs Inputs(LinkState link, BannerKind kind = BannerKind.None, BannerActions actions = BannerActions.None,
                                     bool notice = false, bool dev = false, ConnectionType type = ConnectionType.Serial,
                                     string? port = "COM3")
        => new(new LinkStatus(link), new ControllerBanner(kind, actions, notice, dev), type, port,
               FirmwareStage: null, FirmwareProgress: null, SavePhase: null, Failure: null, OfferTest: false);

    // ── §4.1, one row each ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(LinkState.Disconnected, BannerKind.None, BannerActions.None, false, false,
                "Disconnected", LinkTone.Neutral, "You disconnected Volco from the controller.", CardAction.Connect)]
    [InlineData(LinkState.PortBusy, BannerKind.None, BannerActions.None, false, false,
                "COM3 is in use by another app", LinkTone.Danger, "Close the serial monitor or Arduino IDE.", CardAction.Retry)]
    [InlineData(LinkState.NoAnswer, BannerKind.NoAnswer, BannerActions.CheckFirmware, false, false,
                "No controller answers on COM3", LinkTone.Danger, "It may not have Volco's firmware yet.", CardAction.CheckFirmware)]
    [InlineData(LinkState.Searching, BannerKind.None, BannerActions.None, false, false,
                "Looking for your controller", LinkTone.Neutral, "Plug it in with a USB cable.", CardAction.None)]
    [InlineData(LinkState.Incompatible, BannerKind.Incompatible, BannerActions.Update, true, false,
                "Firmware too old for this Volco", LinkTone.Warning, "Update it to use the controller again.", CardAction.Update)]
    [InlineData(LinkState.Connected, BannerKind.NotSetUp, BannerActions.SetUp, false, false,
                "Not set up yet", LinkTone.Accent, "Choose how to start.", CardAction.None)]
    [InlineData(LinkState.Connected, BannerKind.NotSetUpHasMirror, BannerActions.ApplySaved, false, false,
                "Not set up yet", LinkTone.Accent, "Choose how to start.", CardAction.None)]
    [InlineData(LinkState.Connected, BannerKind.Unusable, BannerActions.SetUpFromScratch, false, false,
                "The controller's setup can't be used", LinkTone.Warning, "Start again from Volco's copy or from scratch.", CardAction.None)]
    [InlineData(LinkState.Connected, BannerKind.BrokenWithFirmware, BannerActions.FixIt, false, false,
                "Your setup doesn't work with this firmware", LinkTone.Warning, "Fix it in the drawing, then Save.", CardAction.FixIt)]
    [InlineData(LinkState.Connected, BannerKind.DifferentSetup, BannerActions.UseControllers, false, false,
                "This controller has a different setup", LinkTone.Warning, "It isn't the one Volco saved for it.", CardAction.Compare)]
    [InlineData(LinkState.Connected, BannerKind.ReadFailed, BannerActions.Retry, false, false,
                "Couldn't read the controller's setup", LinkTone.Danger, null, CardAction.Retry)]
    [InlineData(LinkState.Connected, BannerKind.None, BannerActions.None, true, false,
                "Firmware update available", LinkTone.Warning, "Your setup stays on the controller.", CardAction.Update)]
    [InlineData(LinkState.Connected, BannerKind.None, BannerActions.None, false, false,
                "Connected over USB (COM3)", LinkTone.Good, "Firmware up to date", CardAction.None)]
    [InlineData(LinkState.Connected, BannerKind.None, BannerActions.None, false, true,
                "Connected over USB (COM3)", LinkTone.Good, "Development firmware", CardAction.None)]
    public void EachRowOfTheTable(LinkState link, BannerKind kind, BannerActions actions, bool notice, bool dev,
                                  string status, LinkTone tone, string? detail, CardAction action)
    {
        var card = DeviceCard.From(Inputs(link, kind, actions, notice, dev));

        Assert.Equal(status, card.Status);
        Assert.Equal(tone, card.Tone);
        Assert.Equal(detail, card.Detail);
        Assert.Equal(action, card.Action);
        Assert.Null(card.Progress);
        Assert.Equal(link != LinkState.Disconnected, card.StatusOpensConnection);
    }

    [Theory]
    [InlineData(ConnectionType.Udp, "Connected over Wi-Fi")]
    [InlineData(ConnectionType.WebSocket, "Connected to the simulator")]
    public void ConnectedSaysHow(ConnectionType type, string status)
        => Assert.Equal(status, DeviceCard.From(Inputs(LinkState.Connected, type: type, port: "5005")).Status);

    [Fact]
    public void AFirmwareUpdateShowsItsProgress()
    {
        var card = DeviceCard.From(Inputs(LinkState.Waiting) with { FirmwareStage = "Updating firmware…", FirmwareProgress = 0.62 });

        Assert.Equal("Updating firmware… 62%", card.Status);
        Assert.Equal("Don't unplug the controller.", card.Detail);
        Assert.Equal(CardAction.None, card.Action);
        Assert.Equal(0.62, card.Progress);
        Assert.False(card.StatusOpensConnection);

        var check = DeviceCard.From(Inputs(LinkState.Waiting) with { FirmwareStage = "Checking firmware…" });
        Assert.Equal("Checking firmware…", check.Status);
        Assert.Null(check.Progress);
    }

    [Theory]
    [InlineData("Saving to the controller…")]
    [InlineData("Restarting…")]
    public void ASaveShowsItsPhase(string phase)
    {
        var card = DeviceCard.From(Inputs(LinkState.Waiting) with { SavePhase = phase });

        Assert.Equal(phase, card.Status);
        Assert.Null(card.Detail);
        Assert.Equal(CardAction.None, card.Action);
    }

    [Fact]
    public void FirstMatchWins()
    {
        // A running update beats the port's silence.
        var card = DeviceCard.From(Inputs(LinkState.NoAnswer, BannerKind.NoAnswer, BannerActions.CheckFirmware)
                                   with { FirmwareStage = "Updating firmware…", FirmwareProgress = 0.1 });
        Assert.Equal("Updating firmware… 10%", card.Status);
        Assert.Equal(CardAction.None, card.Action);

        // A different setup beats the firmware notice.
        Assert.Equal(CardAction.Compare,
                     DeviceCard.From(Inputs(LinkState.Connected, BannerKind.DifferentSetup, notice: true)).Action);
    }

    [Fact]
    public void AFailureReplacesTheDetail()
    {
        var card = DeviceCard.From(Inputs(LinkState.Connected) with { Failure = "The controller didn't answer." });

        Assert.Equal("Connected over USB (COM3)", card.Status);
        Assert.Equal("The controller didn't answer.", card.Detail);
    }

    [Fact]
    public void AFailureNeverHidesARunningOperationOrTheDisconnect()
    {
        const string failure = "The controller didn't answer.";
        var update = DeviceCard.From(Inputs(LinkState.Waiting) with
        {
            FirmwareStage = "Updating firmware…", FirmwareProgress = 0.3, Failure = failure,
        });
        Assert.Equal("Don't unplug the controller.", update.Detail);

        var save = DeviceCard.From(Inputs(LinkState.Waiting) with { SavePhase = "Saving to the controller…", Failure = failure });
        Assert.Null(save.Detail);

        var disconnected = DeviceCard.From(Inputs(LinkState.Disconnected) with { Failure = failure });
        Assert.Equal("You disconnected Volco from the controller.", disconnected.Detail);
    }

    [Fact]
    public void NoAnswerOffUsbNamesThePortAndOffersNoCheck()
    {
        var card = DeviceCard.From(Inputs(LinkState.NoAnswer, type: ConnectionType.Udp, port: "5005"));

        Assert.Equal("No controller answers on port 5005", card.Status);
        Assert.Equal(CardAction.None, card.Action);
    }

    [Fact]
    public void AfterAnUpdateOrAFirstSaveTheCardOffersATest()
    {
        var card = DeviceCard.From(Inputs(LinkState.Connected) with { OfferTest = true });

        Assert.Equal(CardAction.TestNow, card.Action);
        Assert.Equal("Test your controller now", DeviceCard.Label(card.Action));
    }

    [Fact]
    public void IncompatibleOverWifiCantUpdate()
        => Assert.Equal(CardAction.None,
                        DeviceCard.From(Inputs(LinkState.Incompatible, BannerKind.Incompatible, type: ConnectionType.Udp)).Action);

    // ── The view model and its ⋯ menu ──────────────────────────────────────────────────────

    private static readonly FirmwareCatalog Catalog = FirmwareCatalog.Load(MemoryFirmwareSource.Valid());
    private static readonly ControllerPreset Diy = ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId);
    private static readonly byte[] DiyBlob = ControllerConfigCodec.Encode(ControllerPresets.ApplyTo(new ControllerConfig(), Diy));

    private readonly TestRig _rig = new();
    private readonly RecordingDispatcher _ui = new();
    private SettingsViewModel? _settings;
    private FakeFlasher? _flasher;

    public void Dispose()
    {
        _settings?.Dispose();
        _rig.Dispose();
    }

    private void Until(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            _ui.RunAll();
            if (condition()) return;
            if (sw.ElapsedMilliseconds > 5000) throw new TimeoutException();
            Thread.Sleep(5);
        }
    }

    /// <summary>The DIY setup, in sync with a connected controller on COM3, with Volco's firmware bundled.</summary>
    private SettingsViewModel InSync()
    {
        var c = _rig.Config.Config;
        c.Connection.Serial.ComPort = "COM3";
        c.Sliders = Enumerable.Range(0, 5).Select(i => new SliderConfig { Index = i }).ToList();
        c.Buttons = Enumerable.Range(0, 3).Select(i => new ButtonConfig { Index = i }).ToList();
        c.ControllerConfigBlob = Convert.ToBase64String(DiyBlob);
        _flasher = new FakeFlasher(() => _rig.Runtime.IsSuspended);
        var firmware = new FirmwareService(_rig.Runtime, _flasher, Catalog);
        _settings = new SettingsViewModel(_rig.Config, _rig.Engine, _rig.Runtime, _ui, firmware);
        _ = _settings.Controls;
        _rig.Connection!.SetStatus(TransportStatus.Open);
        _rig.Connection.RaiseMessage(new HelloMessage(ProtocolVersion.Current, ChipFamily.Esp32, "dev", ControllerConfigState.Stored,
                                                      ControllerConfigCodec.Crc32(DiyBlob), 5,
                                                      new[] { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair }));
        Until(() => _settings.Controller.CanEdit);
        return _settings;
    }

    [Fact]
    public void TheCardFollowsThePage()
    {
        var card = InSync().Hardware.Card;

        Assert.Equal("Volco controller", card.Name);
        Assert.Equal("Connected over USB (COM3)", card.State.Status);
        Assert.Equal("Development firmware", card.State.Detail);
        Assert.True(card.State.StatusOpensConnection);

        card.OpenConnectionCommand.Execute(null);
        Assert.True(_settings!.Hardware.IsConnectionDialogOpen);
        Assert.False(_settings.Hardware.ConnectionDialogOnWifi);

        card.DisconnectCommand.Execute(null);
        Until(() => card.State.Action == CardAction.Connect);
        Assert.Equal("Disconnected", card.State.Status);
        Assert.Equal("Connect", card.ActionLabel);
        Assert.True(card.ActionCommand.CanExecute(null));
    }

    [Fact]
    public void MenuWritersDisabledDuringATest()
    {
        var settings = InSync();
        var card = settings.Hardware.Card;
        Assert.True(card.CheckFirmwareCommand.CanExecute(null));
        Assert.True(card.ReinstallFirmwareCommand.CanExecute(null));
        Assert.True(card.LoadPresetCommand.CanExecute(null));
        Assert.True(card.StartOverCommand.CanExecute(null));

        settings.Controller.StartTestCommand.Execute(null);
        _ui.RunAll();
        Assert.True(settings.Controller.IsTesting);

        Assert.False(card.CheckFirmwareCommand.CanExecute(null));
        Assert.False(card.ReinstallFirmwareCommand.CanExecute(null));
        Assert.False(card.LoadPresetCommand.CanExecute(null));
        Assert.False(card.StartOverCommand.CanExecute(null));
        Assert.True(card.ReconnectCommand.CanExecute(null));
    }

    [Fact]
    public void AProbeThatOffersInstall_MakesTheButtonReinstall_WhichAsksFirst()
    {
        var settings = InSync();
        var card = settings.Hardware.Card;
        _flasher!.ProbeResult = new ProbeResult(ProbeKind.DifferentFirmware);
        card.CheckFirmwareCommand.Execute(null);
        Until(() => card.State.Action == CardAction.Reinstall);
        Assert.Equal("Reinstall firmware…", card.ActionLabel);
        Assert.Equal("This ESP32 doesn't have this version of Volco's firmware.", card.State.Detail);

        Until(() => card.ActionCommand.CanExecute(null));
        card.ActionCommand.Execute(null);
        Assert.True(settings.Controller.ConfirmInstallPending);   // the ⋯ item's confirmation
        Assert.False(settings.Controller.IsFirmwareRunning);

        settings.Controller.CancelInstallCommand.Execute(null);
        settings.Controller.OnPageLeft();                          // the result goes, and the offer with it
        _ui.RunAll();
        Assert.NotEqual(CardAction.Reinstall, card.State.Action);
    }

    [Fact]
    public void DrawingActionsAskForTheControllerPageFirst()
    {
        var settings = InSync();
        var card = settings.Hardware.Card;
        var asked = new List<bool>();   // whether the chooser was already shown when the page was asked for
        card.RequestController += (_, _) => asked.Add(settings.Hardware.ShowChooser);

        card.StartOverCommand.Execute(null);
        Assert.Equal(new[] { false }, asked);
        Assert.True(settings.Hardware.ShowChooser);
        settings.Hardware.CancelStartOver();

        card.LoadPresetCommand.Execute(null);
        Assert.Equal(2, asked.Count);
        card.ClosePresetChooserCommand.Execute(null);

        card.OpenConnectionCommand.Execute(null);   // the Connection dialog opens over any page
        card.ReconnectCommand.Execute(null);
        Assert.Equal(2, asked.Count);
    }

    [Fact]
    public void ReinstallAsksFirst()
    {
        var settings = InSync();
        settings.Hardware.Card.ReinstallFirmwareCommand.Execute(null);

        Assert.True(settings.Controller.ConfirmInstallPending);
        Assert.False(settings.Controller.IsFirmwareRunning);
    }

    [Fact]
    public void LoadAPresetAsksBeforeReplacingControls()
    {
        var settings = InSync();
        var card = settings.Hardware.Card;
        card.LoadPresetCommand.Execute(null);
        Assert.True(card.IsPresetChooserOpen);

        card.ChoosePresetCommand.Execute(ControllerPresets.All.Single(p => p.Id == ControllerPresets.BlankId));
        Assert.False(card.IsPresetChooserOpen);
        Assert.NotNull(settings.Controller.Confirmation);   // the DIY draft has controls
        Assert.Equal(5, settings.Controller.Draft.Sliders.Count);

        settings.Controller.Confirmation!.ConfirmCommand.Execute(null);
        Assert.Empty(settings.Controller.Draft.Sliders);
    }

    [Fact]
    public void StartOverShowsTheChooser()
    {
        var settings = InSync();
        settings.Hardware.Card.StartOverCommand.Execute(null);

        Assert.True(settings.Hardware.ShowChooser);
        Assert.Equal(5, settings.Controller.Draft.Sliders.Count);   // kept until a choice is made
    }
}
