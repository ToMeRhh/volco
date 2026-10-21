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
        var diy = ControllerConfigCodec.Decode(DiyBlob);
        Assert.Equal(5, Hardware.Sliders.Count);
        Assert.Equal($"GPIO {diy.Sliders[0].Pin}", Hardware.Sliders[0].Wiring!.Summary);
        Assert.Equal("Slider 1", Hardware.Sliders[0].Wiring!.Title);
        Assert.Same(_settings!.Controls.Sliders.Items[0], Hardware.Sliders[0]);
        Assert.Equal(3, Hardware.Buttons.Count);
        Assert.Equal($"GPIO {diy.Buttons[0].Pin} · LED 12", Hardware.Buttons[0].Wiring!.Summary);
        Assert.Equal($"GPIO {diy.Buttons[2].Pin} · LEDs 18, 19", Hardware.Buttons[2].Wiring!.Summary);
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
        Assert.False(Hardware.Card.StartOverCommand.CanExecute(null));
        Hardware.StartOver();
        Assert.False(Hardware.ShowChooser);

        GreetInSync(DiyBlob);
        Assert.True(Hardware.Card.StartOverCommand.CanExecute(null));
    }

    // ── The Connection dialog ────────────────────────────────────────────────────────────────

    [Fact]
    public void AClosedConnectionDialogStopsFollowingTheConnection()
    {
        SetUpInSync();
        Hardware.OpenConnection(false);
        var dialog = Hardware.ConnectionDialog!;
        var raised = 0;
        dialog.PropertyChanged += (_, _) => raised++;
        Hardware.CloseConnection();

        Controller.Connection.ConnectionTypeIndex = 1;
        Assert.Equal(0, raised);
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
}
