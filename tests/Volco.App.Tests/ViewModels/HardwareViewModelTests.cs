using System.Diagnostics;
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

/// <summary>Hardware-page spec §5.1, §5.2, §5.4 and §6.5: the drawing follows the draft, and a
/// removed control shifts Volco's settings only once the controller confirms the save.</summary>
public sealed class HardwareViewModelTests : IDisposable
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
    private static readonly IndicatorKind[] DiyKinds = { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair };

    private FakeControllerConnection Conn => _rig.Connection!;
    private AppConfig Config => _rig.Config.Config;
    private ControllerViewModel Controller => _settings!.Controller;
    private HardwareViewModel Hardware => _settings!.Hardware;

    /// <summary>Volco's sliders are named a..e; the mirror is <paramref name="mirror"/>.</summary>
    private SettingsViewModel Settings(byte[]? mirror)
    {
        Config.Sliders = "abcde".Select((c, i) => new SliderConfig { Index = i, Name = c.ToString() }).ToList();
        Config.Buttons = Enumerable.Range(0, 3).Select(i => new ButtonConfig { Index = i }).ToList();
        if (mirror is not null) Config.ControllerConfigBlob = Convert.ToBase64String(mirror);
        _settings = _rig.CreateSettings(_ui);
        _ = _settings.Controls;
        return _settings;
    }

    private static HelloMessage Hello(ControllerConfigState state, uint crc, int sliders = 5, IndicatorKind[]? kinds = null)
        => new(ProtocolVersion.Current, ChipFamily.Esp32, "dev", state, crc, sliders, kinds ?? DiyKinds);

    private void GreetInSync(byte[] mirror)
    {
        Conn.SetStatus(TransportStatus.Open);
        Conn.RaiseMessage(Hello(ControllerConfigState.Stored, ControllerConfigCodec.Crc32(mirror)));
        _ui.RunAll();
    }

    private void Until(Func<bool> condition, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            _ui.RunAll();
            if (condition()) return;
            if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("condition not met");
            Thread.Sleep(5);
        }
    }

    private int PinWatchFrames(PinWatchRole role)
    {
        var frame = ProtocolEncoder.EncodePinWatch(role);
        return Conn.SentFrames.Count(f => f.SequenceEqual(frame));
    }

    private PinFieldViewModel OpenSliderPinList()
    {
        Settings(DiyBlob);
        GreetInSync(DiyBlob);
        var field = Hardware.PinField(new PinOwner(PinOwnerKind.Slider, 0));
        field.Open();
        Until(() => PinWatchFrames(PinWatchRole.Analog) == 1);
        Assert.Equal(0, PinWatchFrames(PinWatchRole.Stop));
        return field;
    }

    [Fact]
    public void ClosePinDialog_StopsTheWatchOfAnOpenDialog()
    {
        var field = OpenSliderPinList();
        Hardware.ClosePinDialog();
        Until(() => PinWatchFrames(PinWatchRole.Stop) == 1);
        Assert.False(field.IsOpen);
    }

    [Fact]
    public void PinDialog_IsTheOpenField_AndOpeningAnotherClosesIt()
    {
        var field = OpenSliderPinList();
        Assert.Same(field, Hardware.PinDialog);
        var button = Hardware.PinField(new PinOwner(PinOwnerKind.Button, 0));
        button.Open();
        Assert.False(field.IsOpen);
        Assert.Same(button, Hardware.PinDialog);
        button.Cancel();
        Assert.Null(Hardware.PinDialog);
    }

    [Fact]
    public void DisposingTheHardwareModel_StopsTheWatchOfAnOpenList()
    {
        OpenSliderPinList();
        Hardware.Dispose();
        Until(() => PinWatchFrames(PinWatchRole.Stop) == 1);
    }

    private byte[] LastWriteBurst() => Conn.SentFrames.Last(f => f.Length > 3 && f[2] == 0x88);

    private void WaitForWrite() => Until(() => Conn.SentFrames.Any(f => f.Length > 3 && f[2] == 0x88));

    /// <summary>Saves the draft and answers with OK and a Hello of the saved setup.</summary>
    private void SaveAndConfirm()
    {
        var blob = ControllerConfigCodec.Encode(Controller.Draft);
        Controller.SaveCommand.Execute(null);
        WaitForWrite();
        Conn.RaiseMessage(new ConfigResultMessage(LastWriteBurst()[3], ConfigResultCode.Ok, 0xFF, 0xFF));
        Conn.RaiseMessage(Hello(ControllerConfigState.Stored, ControllerConfigCodec.Crc32(blob),
                                sliders: Controller.Draft.Sliders.Count));
        Until(() => !Controller.IsSaving);
        _ui.RunAll();
    }

    private string[] SavedNames() => Config.Sliders.Select(s => s.Name).ToArray();

    private void SetUpInSync()
    {
        Settings(DiyBlob);
        GreetInSync(DiyBlob);
        Assert.True(Controller.CanEdit);
    }

    // ── The drawing ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryControlsTileHasItsWiringFromTheStart()
    {
        Settings(DiyBlob);
        Assert.All(_settings!.Controls.Sliders.Items, s => Assert.NotNull(s.Wiring));
        Assert.All(_settings.Controls.Buttons.Items, b => Assert.NotNull(b.Wiring));
    }

    [Fact]
    public void TilesFollowTheDraft()
    {
        SetUpInSync();
        Assert.Equal(5, Hardware.Sliders.Count);
        Assert.Equal("Slider 1", Hardware.Sliders[0].Wiring!.Title);
        Assert.Same(_settings!.Controls.Sliders.Items[0], Hardware.Sliders[0]);
        Assert.Equal(3, Hardware.Buttons.Count);
        Assert.Equal("Button 3", Hardware.Buttons[2].Wiring!.Title);
        Assert.False(Hardware.IsReadOnly);
    }

    [Fact]
    public void ChangedTileHasADot()
    {
        SetUpInSync();
        Controller.SetSliderPin(1, 39);
        Assert.True(Hardware.Sliders[1].Wiring!.HasChange);
        Assert.All(Hardware.Sliders.Where((_, i) => i != 1), s => Assert.False(s.Wiring!.HasChange));
        Assert.All(Hardware.Buttons, b => Assert.False(b.Wiring!.HasChange));
    }

    [Fact]
    public void AProblemIsOnItsTile()
    {
        SetUpInSync();
        Controller.SetSliderPin(1, 33);   // Slider 1's pin
        Assert.True(Hardware.Sliders[0].Wiring!.HasProblem);
        Assert.True(Hardware.Sliders[1].Wiring!.HasProblem);
        Assert.False(Hardware.Sliders[2].Wiring!.HasProblem);
    }

    // ── Removing and adding (spec §6.5) ──────────────────────────────────────────────────────

    [Fact]
    public void RemovingAMiddleSliderRenumbersAndKeepsTargets()
    {
        SetUpInSync();
        Controller.RemoveSlider(2);
        Assert.Equal(new[] { "Slider 1", "Slider 2", "Slider 3", "Slider 4" }, Hardware.Sliders.Select(s => s.Wiring!.Title));
        Assert.Equal(new[] { "a", "b", "d", "e" }, Hardware.Sliders.Select(s => s.Config.Name));
        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, SavedNames());
        Assert.Equal(5, _settings!.Controls.Sliders.Items.Count);
    }

    [Fact]
    public void ConfirmedSaveShiftsTheSettings()
    {
        SetUpInSync();
        var shifted = 0;
        Controller.ControlsShifted += (_, _) => shifted++;
        Controller.RemoveSlider(2);
        SaveAndConfirm();
        Assert.Equal(new[] { "a", "b", "d", "e" }, SavedNames());
        Assert.Equal(1, shifted);
        Assert.Equal(4, _settings!.Controls.Sliders.Items.Count);
        Assert.True(Controller.Layout.IsIdentity);
        Assert.Equal(_settings.Controls.Sliders.Items, Hardware.Sliders);
        Assert.Equal(new[] { "a", "b", "d", "e" }, Hardware.Sliders.Select(s => s.Config.Name));
    }

    [Fact]
    public void TheDraftCantChangeDuringASave_SoTheSettingsMatchWhatWasSaved()
    {
        SetUpInSync();
        Controller.RemoveSlider(2);
        var blob = ControllerConfigCodec.Encode(Controller.Draft);
        Controller.SaveCommand.Execute(null);
        Assert.True(Controller.IsSaving);
        Assert.False(Controller.CanEdit);
        Assert.True(Hardware.IsReadOnly);

        Controller.RemoveSlider(0);                    // refused while saving
        Controller.AddSliderCommand.Execute(null);
        Assert.Equal(4, Hardware.Sliders.Count);
        Assert.Equal(blob, ControllerConfigCodec.Encode(Controller.Draft));

        WaitForWrite();
        Conn.RaiseMessage(new ConfigResultMessage(LastWriteBurst()[3], ConfigResultCode.Ok, 0xFF, 0xFF));
        Conn.RaiseMessage(Hello(ControllerConfigState.Stored, ControllerConfigCodec.Crc32(blob), sliders: 4));
        Until(() => !Controller.IsSaving);
        _ui.RunAll();
        Assert.Equal(new[] { "a", "b", "d", "e" }, SavedNames());
        Assert.True(Controller.Layout.IsIdentity);
        Assert.True(Controller.CanEdit);
        Assert.Equal(new[] { "a", "b", "d", "e" }, Hardware.Sliders.Select(s => s.Config.Name));
    }

    [Fact]
    public void FailedSaveLeavesTheSettings()
    {
        SetUpInSync();
        Controller.RemoveSlider(2);
        Controller.SaveCommand.Execute(null);
        WaitForWrite();
        Conn.RaiseMessage(new ConfigResultMessage(LastWriteBurst()[3], ConfigResultCode.StorageFailed, 0xFF, 0xFF));
        Until(() => !Controller.IsSaving);
        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, SavedNames());
        Assert.Equal(5, _settings!.Controls.Sliders.Items.Count);
        Assert.Equal(4, Hardware.Sliders.Count);   // the draft is still the edited one
    }

    [Fact]
    public void DiscardLeavesTheSettings()
    {
        SetUpInSync();
        Controller.RemoveSlider(2);
        Controller.RevertCommand.Execute(null);
        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, SavedNames());
        Assert.Equal(5, Hardware.Sliders.Count);
        Assert.True(Controller.Layout.IsIdentity);
        Assert.Equal(_settings!.Controls.Sliders.Items, Hardware.Sliders);
    }

    [Fact]
    public void AddedSliderIsDetachedUntilSaved()
    {
        SetUpInSync();
        Controller.AddSliderCommand.Execute(null);
        Controller.SetSliderPin(5, 39);
        Assert.Equal(6, Hardware.Sliders.Count);
        Assert.Equal(5, Config.Sliders.Count);
        Assert.DoesNotContain(Hardware.Sliders[5], _settings!.Controls.Sliders.Items);
        Assert.Equal("Slider 6", Hardware.Sliders[5].Wiring!.Title);
        Assert.True(Hardware.Sliders[5].Wiring!.HasChange);

        SaveAndConfirm();
        Assert.Equal(6, Config.Sliders.Count);
        Assert.Equal(6, _settings.Controls.Sliders.Items.Count);
        Assert.Equal(_settings.Controls.Sliders.Items, Hardware.Sliders);
    }

    // ── + Add opens the pin dialog to add (spec §6.3) ───────────────────────────────────────

    [Fact]
    public void AddSlider_SelectsItAndOpensItsPinDialogListening()
    {
        SetUpInSync();
        Hardware.AddSliderCommand.Execute(null);
        Assert.Equal(6, Hardware.Sliders.Count);
        Assert.Same(Hardware.Sliders[5], _settings!.Controls.Selected);
        var field = Assert.IsType<PinFieldViewModel>(Hardware.PinDialog);
        Assert.Same(Hardware.PinField(new PinOwner(PinOwnerKind.Slider, 5)), field);
        Assert.True(field is { IsAdding: true, IsListView: true, IsWaiting: true, CanConfirm: false });
        Assert.Equal("Add Slider 6", field.Title);
        Assert.Equal("Add slider", field.ConfirmLabel);
        Assert.Equal("Listening for slider movements…", field.WatchHint);
        Until(() => PinWatchFrames(PinWatchRole.Analog) == 1);
    }

    [Fact]
    public void AddSlider_TheMovedPinShowsUp_AddGivesItThePin()
    {
        SetUpInSync();
        Hardware.AddSliderCommand.Execute(null);
        var field = Hardware.PinDialog!;
        Conn.RaiseMessage(new PinActivityMessage(32));
        Until(() => field.Heard.Count == 1);
        Assert.Equal(32u, field.Heard[0].Pin);
        Assert.True(field.CanConfirm);   // the first heard is selected

        field.Confirm();
        Assert.Null(Hardware.PinDialog);
        Assert.Equal(6, Hardware.Sliders.Count);
        Assert.Equal(32u, Controller.Draft.Sliders[5].Pin);
        Assert.Same(Hardware.Sliders[5], _settings!.Controls.Selected);
    }

    [Fact]
    public void AddButton_Cancel_RemovesItAndGivesTheSelectionBack()
    {
        SetUpInSync();
        var controls = _settings!.Controls;
        controls.Select(Hardware.Sliders[1]);
        Hardware.AddButtonCommand.Execute(null);
        Assert.Equal(4, Hardware.Buttons.Count);
        var field = Hardware.PinDialog!;
        Assert.Equal(("Add Button 4", "Add button", "Listening for button presses…"), (field.Title, field.ConfirmLabel, field.WatchHint));

        field.Cancel();
        Assert.Null(Hardware.PinDialog);
        Assert.Equal(3, Hardware.Buttons.Count);
        Assert.Equal(3, Controller.Draft.Buttons.Count);
        Assert.False(Controller.IsDirty);
        Assert.Same(Hardware.Sliders[1], controls.Selected);
    }

    [Fact]
    public void AddSlider_ClosingThePinDialogAnyOtherWay_RemovesIt()
    {
        SetUpInSync();
        Hardware.AddSliderCommand.Execute(null);
        Hardware.ClosePinDialog();   // Escape, the scrim, the window hidden, the page left
        Assert.Equal(5, Hardware.Sliders.Count);
        Assert.False(Controller.IsDirty);
    }

    // ── The selection Controls gets back (Controls has no detached tiles, bus or settings) ───

    [Fact]
    public void ADiscardedAddedSlider_DoesntStaySelected_ControlsSelectsSlider1()
    {
        SetUpInSync();
        var controls = _settings!.Controls;
        Controller.AddSliderCommand.Execute(null);
        var added = Hardware.Sliders[5];
        controls.Select(added);
        Assert.IsType<SliderWiringViewModel>(Hardware.Inspected);

        Controller.RevertCommand.Execute(null);
        Assert.NotSame(added, controls.Selected);
        Hardware.OnPageLeft();   // the page is left for General
        Assert.Same(controls.Sliders.Items[0], controls.Selected);
    }

    [Fact]
    public void AStartOverPickDropsTheDetachedSelection()
    {
        SetUpInSync();
        var controls = _settings!.Controls;
        Controller.AddSliderCommand.Execute(null);
        controls.Select(Hardware.Sliders[5]);
        Hardware.StartOver();
        Hardware.Chooser.Cards[^1].PickCommand.Execute(null);   // Start empty
        Assert.Null(controls.Selected);
        Hardware.OnPageLeft();
        Assert.Same(controls.Sliders.Items[0], controls.Selected);
    }

    [Fact]
    public void TheSettingsChip_ThenGeneral_SelectsTheDefault()
    {
        SetUpInSync();
        var controls = _settings!.Controls;
        controls.Select(controls.Sliders.Items[2]);
        Hardware.SelectControllerSettings();
        Assert.Null(controls.Selected);
        Hardware.OnPageLeft();
        Assert.Same(controls.Sliders.Items[0], controls.Selected);
    }

    // ── Start over (spec §7.1) ───────────────────────────────────────────────────────────────

    [Fact]
    public void StartOverCanBeCancelled_AndHidesTheSaveBar()
    {
        SetUpInSync();
        Controller.SetSliderPin(1, 39);
        Assert.True(Hardware.ShowSaveBar);

        Hardware.StartOver();
        Assert.True(Hardware.ShowChooser);
        Assert.True(Hardware.Chooser.CanGoBack);
        Assert.False(Hardware.ShowSaveBar);   // the chooser is in the drawing's place

        Hardware.Chooser.BackCommand.Execute(null);   // Back (Escape does the same)
        Assert.False(Hardware.ShowChooser);
        Assert.False(Hardware.Chooser.CanGoBack);
        Assert.True(Hardware.ShowSaveBar);
        Assert.Equal(39u, Controller.Draft.Sliders[1].Pin);   // the draft as it was
    }

    [Fact]
    public void DiscardOrLeavingEndsAStartOver()
    {
        SetUpInSync();
        Hardware.StartOver();
        Controller.RevertCommand.Execute(null);
        Assert.False(Hardware.ShowChooser);

        Hardware.StartOver();
        Controller.OnPageLeft();   // another page, or the window hidden
        Assert.False(Hardware.ShowChooser);
        Assert.False(Hardware.IsStartingOver);
    }

    [Fact]
    public void StartOverNeedsAReadyController()
    {
        Settings(DiyBlob);   // offline: the mirror, read-only
        Assert.False(Hardware.Status.StartOverCommand.CanExecute(null));
        Hardware.StartOver();
        Assert.False(Hardware.ShowChooser);

        GreetInSync(DiyBlob);
        Assert.True(Hardware.Status.StartOverCommand.CanExecute(null));
    }

    // ── The Connection dialog ────────────────────────────────────────────────────────────────

    [Fact]
    public void AClosedConnectionDialogStopsFollowingTheConnection()
    {
        SetUpInSync();
        Hardware.OpenConnection(false);
        var dialog = Hardware.ConnectionDialog!;
        var raised = 0;
        dialog.UsbCard.PropertyChanged += (_, _) => raised++;
        Controller.Connection.ConnectionTypeIndex = 2;   // open, the card follows
        Assert.NotEqual(0, raised);
        Hardware.CloseConnection();
        raised = 0;

        Controller.Connection.ConnectionTypeIndex = 0;
        Assert.Equal(0, raised);
    }

    [Fact]
    public void SaveAndContinueThenOpensConnection()
    {
        SetUpInSync();
        Controller.DebounceMs = 25;
        Hardware.OpenConnection(false);
        var blob = ControllerConfigCodec.Encode(Controller.Draft);
        Hardware.LeavePrompt!.SaveAndContinueCommand.Execute(null);
        WaitForWrite();
        Assert.False(Hardware.IsConnectionDialogOpen);   // not before the controller confirms
        Conn.RaiseMessage(new ConfigResultMessage(LastWriteBurst()[3], ConfigResultCode.Ok, 0xFF, 0xFF));
        Conn.RaiseMessage(Hello(ControllerConfigState.Stored, ControllerConfigCodec.Crc32(blob), sliders: Controller.Draft.Sliders.Count));
        Until(() => !Controller.IsSaving);
        _ui.RunAll();
        Assert.Null(Hardware.LeavePrompt);
        Assert.True(Hardware.IsConnectionDialogOpen);
        Assert.False(Hardware.HasUnsavedControllerChanges);
    }

    [Fact]
    public void AFaultedWifiOperationCountsAsSaveFailed()
    {
        Assert.Equal(WifiSwitchOutcome.SaveFailed, WifiOutcomes.Of(Task.FromException<WifiSwitchOutcome>(new InvalidOperationException("boom"))));
        Assert.Equal(WifiSwitchOutcome.SaveFailed, WifiOutcomes.Of(Task.FromCanceled<WifiSwitchOutcome>(new CancellationToken(true))));
        Assert.Equal(WifiSwitchOutcome.OnWifi, WifiOutcomes.Of(Task.FromResult(WifiSwitchOutcome.OnWifi)));

        // The progress dialog a faulted save opened still ends.
        SetUpInSync();
        var progress = Hardware.Status.Progress;
        progress.BeginWifi("Saving to the controller", "Sending…");
        WifiOutcomes.OnEnd(Task.FromException<WifiSwitchOutcome>(new InvalidOperationException("boom")), _ui,
                           o => progress.EndWifi(WifiOutcomes.FailureText(o, false), LinkTone.Danger));
        Until(() => { _ui.RunAll(); return progress.IsDone; });
        Assert.Equal("The controller didn't save the Wi-Fi settings. Try again.", progress.ResultText);
    }

    [Fact]
    public void TheConnectionDialogOpensOnTheCardInUse()
    {
        SetUpInSync();
        Hardware.OpenConnection(false);
        var dialog = Hardware.ConnectionDialog!;
        Assert.Equal(new[] { true, false, false }, dialog.Cards.Select(c => c.IsOpen));
        Assert.Equal(new[] { true, false, false }, dialog.Cards.Select(c => c.IsInUse));

        dialog.Open(ConnectionCardKind.Simulator);
        Assert.Equal(new[] { false, false, true }, dialog.Cards.Select(c => c.IsOpen));
        Assert.Equal(ConnectionType.Serial, Controller.Connection.ConnectionType);   // opening switches nothing

        Hardware.OpenConnection(true);
        Assert.True(Hardware.ConnectionDialog!.WifiCard.IsOpen);
        Assert.False(Hardware.ConnectionDialog.UsbCard.IsOpen);
    }

    [Fact]
    public void OpenConnectionAsksFirstWithUnsavedControllerChanges()
    {
        SetUpInSync();
        Controller.DebounceMs = 25;
        Hardware.OpenConnection(true);
        Assert.False(Hardware.IsConnectionDialogOpen);
        var prompt = Hardware.LeavePrompt!;
        Assert.Equal("Your controller changes aren't saved. Save them before opening Connection?", prompt.Message);
        Assert.Equal(("Save and continue", "Discard and continue", "Stay"), (prompt.SaveLabel, prompt.DiscardLabel, prompt.StayLabel));

        prompt.StayCommand.Execute(null);
        Assert.Null(Hardware.LeavePrompt);
        Assert.False(Hardware.IsConnectionDialogOpen);
        Assert.True(Hardware.HasUnsavedControllerChanges);

        Hardware.OpenConnection(true);
        Hardware.LeavePrompt!.DiscardAndContinueCommand.Execute(null);
        Assert.True(Hardware.IsConnectionDialogOpen);
        Assert.True(Hardware.ConnectionDialog!.WifiCard.IsOpen);
    }

    [Fact]
    public void ButtonTimingAloneDoesNotAskBeforeConnection()
    {
        SetUpInSync();
        Controller.HoldMs = 700;   // Volco's own setting, not the controller's
        Hardware.OpenConnection(false);
        Assert.Null(Hardware.LeavePrompt);
        Assert.True(Hardware.IsConnectionDialogOpen);
    }

    // ── Raw input and read-only ──────────────────────────────────────────────────────────────

    [Fact]
    public void RawInputMovesTheFaderOutsideATest()
    {
        SetUpInSync();
        var before = _rig.Engine.Snapshot(_rig.Engine.DeviceVolumeCalls).Count;
        Conn.RaiseMessage(new SliderMessage(1, 2048));
        _ui.RunAll();
        Assert.Equal(0.5, Hardware.Sliders[1].Wiring!.RawLevel, 2);
        Assert.False(_rig.Runtime.InTestMode);
        // The processed path still ran: the main output's volume was set.
        var sw = Stopwatch.StartNew();
        while (_rig.Engine.Snapshot(_rig.Engine.DeviceVolumeCalls).Count == before && sw.ElapsedMilliseconds < 5000) Thread.Sleep(5);
        Assert.True(_rig.Engine.Snapshot(_rig.Engine.DeviceVolumeCalls).Count > before);
    }

    [Fact]
    public void OfflineShowsTheMirrorReadOnly()
    {
        Settings(DiyBlob);
        Assert.Equal(5, Hardware.Sliders.Count);
        Assert.True(Hardware.IsReadOnly);
    }

    [Fact]
    public void OfflineWithoutAMirrorShowsNoTiles()
    {
        Settings(mirror: null);
        Assert.Empty(Hardware.Sliders);
        Assert.Empty(Hardware.Buttons);
        Assert.True(Hardware.IsReadOnly);
        Assert.All(_settings!.Controls.Sliders.Items, s => Assert.NotNull(s.Wiring));
    }
    // ── Try the lights (hardware-page spec §6.2) ─────────────────────────────────────────────

    private ButtonWiringViewModel SelectButton(int index)
    {
        Settings(DiyBlob);
        GreetInSync(DiyBlob);
        _settings!.Controls.Select(_settings.Controls.Buttons.Items[index]);
        return Assert.IsType<ButtonWiringViewModel>(Hardware.Inspected);
    }

    private List<byte[]> Drives() => Conn.SentFrames.Where(f => f.Length > 2 && f[2] == (byte)MessageType.PinDrive).ToList();

    private static byte[] Drive(int pin, bool high, int ms) => ProtocolEncoder.EncodePinDrive(pin, high, (ushort)ms);

    [Fact]
    public void TryLights_SingleLed_SendsLitUnlitLitOnItsDraftPin()
    {
        var button = SelectButton(0);
        DraftPins.Set(Controller.Draft, new PinOwner(PinOwnerKind.Light, 0), 12);   // not saved
        Controller.OnDraftEdited();
        button.LitWhenHigh = true;
        Assert.True(button.TryLightsCommand.CanExecute(null));

        button.TryLightsCommand.Execute(null);
        Until(() => Drives().Count == 3);
        Assert.Equal(new[] { Drive(12, true, 300), Drive(12, false, 300), Drive(12, true, 300) }, Drives());

        button.LitWhenHigh = false;
        button.TryLightsCommand.Execute(null);
        Until(() => Drives().Count == 6);
        Assert.Equal(new[] { Drive(12, false, 300), Drive(12, true, 300), Drive(12, false, 300) }, Drives().Skip(3));
    }

    [Fact]
    public void TryLights_Pair_SendsAThenB()
    {
        var button = SelectButton(2);
        DraftPins.Set(Controller.Draft, new PinOwner(PinOwnerKind.LightA, 2), 4);
        DraftPins.Set(Controller.Draft, new PinOwner(PinOwnerKind.LightB, 2), 18);
        Controller.OnDraftEdited();

        button.TryLightsCommand.Execute(null);

        Until(() => Drives().Count == 4);
        Assert.Equal(new[] { Drive(4, true, 600), Drive(18, false, 600), Drive(18, true, 600), Drive(4, false, 600) }, Drives());
    }

    [Fact]
    public void TryLights_EnabledOnceTheControllerIsReady()
    {
        Settings(DiyBlob);
        _settings!.Controls.Select(_settings.Controls.Buttons.Items[0]);
        var button = Assert.IsType<ButtonWiringViewModel>(Hardware.Inspected);
        int changed = 0;
        button.TryLightsCommand.CanExecuteChanged += (_, _) => changed++;
        Assert.False(button.TryLightsCommand.CanExecute(null));
        Assert.False(Controller.CanEdit);

        // Another setup than Volco's copy: the controller is ready, the draft stays read-only.
        Conn.SetStatus(TransportStatus.Open);
        Conn.RaiseMessage(Hello(ControllerConfigState.Stored, ControllerConfigCodec.Crc32(DiyBlob) ^ 1));
        _ui.RunAll();

        Assert.False(Controller.CanEdit);
        Assert.Same(button, Hardware.Inspected);
        Assert.True(changed > 0);
        Assert.True(button.TryLightsCommand.CanExecute(null));
    }

    [Fact]
    public void TryLights_DisabledWhenAPinIsNotSet()
    {
        var button = SelectButton(2);
        Assert.True(button.TryLightsCommand.CanExecute(null));

        DraftPins.Set(Controller.Draft, new PinOwner(PinOwnerKind.LightB, 2), null);
        Controller.OnDraftEdited();

        Assert.False(button.TryLightsCommand.CanExecute(null));
        button.TryLightsCommand.Execute(null);
        Assert.Empty(Drives());
    }

    [Fact]
    public void TryLights_HiddenForScreenAndNone()
    {
        var button = SelectButton(0);
        Assert.True(button.HasLeds);

        button.LightIndex = 3;
        Assert.False(button.HasLeds);
        button.LightIndex = 0;
        Assert.False(button.HasLeds);
        Assert.False(button.TryLightsCommand.CanExecute(null));
    }
}
