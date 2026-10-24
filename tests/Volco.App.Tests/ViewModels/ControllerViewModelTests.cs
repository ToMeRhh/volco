using System.Diagnostics;
using Volco.App.ViewModels;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Volco.Core.Runtime;
using Volco.Core.Tests.Runtime;
using Xunit;

namespace Volco.App.Tests.ViewModels;

/// <summary>Spec §8.4 and §12.4 (hardware-page spec §6.5): the controller draft's editing model,
/// against a real runtime over a fake connection.</summary>
public sealed class ControllerViewModelTests : IDisposable
{
    private readonly TestRig _rig = new();
    private readonly RecordingDispatcher _ui = new();
    private ControllerViewModel? _vm;

    public void Dispose()
    {
        _vm?.Dispose();
        _rig.Dispose();
    }

    private static ControllerPreset Diy => ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId);
    private static ControllerPreset Blank => ControllerPresets.All.Single(p => p.Id == ControllerPresets.BlankId);
    private static readonly byte[] DiyBlob = ControllerConfigCodec.Encode(ControllerPresets.ApplyTo(new ControllerConfig(), Diy));
    private static readonly IndicatorKind[] DiyKinds = { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair };

    private FakeControllerConnection Conn => _rig.Connection!;

    private ControllerViewModel Vm(byte[]? mirror = null)
    {
        if (mirror is not null) _rig.Config.Config.ControllerConfigBlob = Convert.ToBase64String(mirror);
        _vm = new ControllerViewModel(_rig.Config, _rig.Runtime, _ui);
        return _vm;
    }

    private static HelloMessage Hello(ControllerConfigState state, uint crc, int sliders = 5, IndicatorKind[]? kinds = null)
        => new(ProtocolVersion.Current, ChipFamily.Esp32, "dev", state, crc, sliders, kinds ?? DiyKinds);

    private void Greet(HelloMessage hello)
    {
        Conn.SetStatus(TransportStatus.Open);
        Conn.RaiseMessage(hello);
        _ui.RunAll();
    }

    private void GreetInSync(byte[] mirror) => Greet(Hello(ControllerConfigState.Stored, ControllerConfigCodec.Crc32(mirror)));

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

    /// <summary>Answers the last write burst with OK and a confirming Hello.</summary>
    private void ConfirmLastWrite(byte[] blob)
    {
        Until(() => Conn.SentFrames.Any(f => f.Length > 3 && f[2] == 0x88));
        Conn.RaiseMessage(new ConfigResultMessage(LastWriteBurst()[3], ConfigResultCode.Ok, 0xFF, 0xFF));
        Conn.RaiseMessage(Hello(ControllerConfigState.Stored, ControllerConfigCodec.Crc32(blob)));
    }

    // ── The editing-allowed rule ───────────────────────────────────────────────────────────

    [Fact]
    public void NotReady_ReadOnly_ShowingTheMirror()
    {
        var vm = Vm(DiyBlob);
        Assert.False(vm.CanEdit);
        Assert.Equal(5, vm.Draft.Sliders.Count);
    }

    [Fact]
    public void InSync_Editable()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        Assert.True(vm.InSync);
        Assert.True(vm.CanEdit);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void ADifferentSetup_ReadOnly()
    {
        var vm = Vm(DiyBlob);
        Greet(Hello(ControllerConfigState.Stored, 0x1234));
        Assert.Equal(BannerKind.DifferentSetup, vm.Banner.Kind);
        Assert.False(vm.CanEdit);
    }

    [Fact]
    public void SetUp_GrantsEditing_UntilADifferentHello()
    {
        var vm = Vm();
        Greet(Hello(ControllerConfigState.None, 0));
        Assert.Equal(BannerKind.NotSetUp, vm.Banner.Kind);
        Assert.False(vm.CanEdit);
        vm.StartFromScratchCommand.Execute(null);
        Assert.True(vm.CanEdit);

        Conn.RaiseMessage(Hello(ControllerConfigState.None, 0));   // the same Hello: still allowed
        _ui.RunAll();
        Assert.True(vm.CanEdit);

        Conn.RaiseMessage(Hello(ControllerConfigState.Damaged, 0x77));
        _ui.RunAll();
        Assert.False(vm.CanEdit);
    }

    // ── Onboarding ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SetupFromAPreset_StartsADraftThatMustBeSaved()
    {
        var vm = Vm();
        Greet(Hello(ControllerConfigState.None, 0));
        Assert.Equal(Diy, vm.SetupPresets[0]);
        vm.StartFromPresetCommand.Execute(Diy);
        Assert.True(vm.CanEdit);
        Assert.Equal(5, vm.Draft.Sliders.Count);
        Assert.True(vm.IsSettingUp);
        Assert.True(vm.IsDirty);
        Assert.True(vm.CanSave);
    }

    [Fact]
    public void SettingUp_ADraftEqualToVolcosSavedCopy_IsStillUnsaved_UntilTheControllerHasIt()
    {
        // An erased controller, and Volco still has the same setup saved: the draft equals the
        // mirror, but the controller doesn't hold it, so Save to controller must stay.
        var vm = Vm(DiyBlob);
        Greet(Hello(ControllerConfigState.None, 0));
        Assert.Equal(BannerKind.NotSetUpHasMirror, vm.Banner.Kind);
        vm.StartFromPresetCommand.Execute(Diy);
        Assert.Equal(DiyBlob, ControllerConfigCodec.Encode(vm.Draft));
        Assert.True(vm.IsDirty);
        Assert.True(vm.CanSave);

        vm.SaveCommand.Execute(null);
        ConfirmLastWrite(DiyBlob);
        Until(() => !vm.IsSaving);
        Assert.Equal("Saved. The controller is set up: press Test controller to try it.", vm.ProgressText);
        Assert.True(vm.InSync);
        Assert.False(vm.IsSettingUp);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void SetupApplySaved_SendsTheMirrorAtOnce_NoDraft()
    {
        var vm = Vm(DiyBlob);
        Greet(Hello(ControllerConfigState.None, 0));
        vm.SetupApplySavedCommand.Execute(null);
        Until(() => Conn.SentFrames.Any(f => f.Length > 3 && f[2] == 0x88));
        Assert.False(vm.HasUnsavedChanges);
    }

    // ── Dirty ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AMirrorWithUnknownFields_IsNotDirty_AndAPinChangeIs()
    {
        var mirror = new byte[] { 0x98, 0x06, 0x01 }.Concat(DiyBlob).ToArray();   // field 99, unknown to this app
        var vm = Vm(mirror);
        GreetInSync(mirror);
        Assert.False(vm.IsDirty);

        vm.SetSliderPin(0, 39);
        Assert.True(vm.IsDirty);
        Assert.True(vm.CanSave);
        vm.SetSliderPin(0, 33);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void AMirrorChange_RebasesACleanDraft_KeepsADirtyOne()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);

        var changed = ControllerPresets.ApplyTo(new ControllerConfig(), Diy);
        changed.Sliders[0].Pin = 39;
        var blob1 = ControllerConfigCodec.Encode(changed);
        var save1 = _rig.Runtime.ControllerConfig.SaveAsync(blob1, TestContext.Current.CancellationToken);
        ConfirmLastWrite(blob1);
        Until(() => save1.IsCompleted && vm.Draft.Sliders.Count == 5 && vm.Draft.Sliders[0].Pin == 39);
        Assert.False(vm.IsDirty);
        Assert.Null(vm.Notice);

        vm.SetSliderPin(1, 34);   // dirty now
        changed.Sliders[0].Pin = 33;
        var blob2 = ControllerConfigCodec.Encode(changed);
        var save2 = _rig.Runtime.ControllerConfig.SaveAsync(blob2, TestContext.Current.CancellationToken);
        ConfirmLastWrite(blob2);
        Until(() => save2.IsCompleted && vm.BaselineConfig().Sliders[0].Pin == 33);
        Assert.Equal(34u, vm.Draft.Sliders[1].Pin);   // the dirty draft stays
        Assert.True(vm.IsDirty);
        Assert.Equal("The controller's setup changed. Discard to load it.", vm.Notice);

        vm.RevertCommand.Execute(null);
        Assert.Null(vm.Notice);
        Assert.Equal(33u, vm.Draft.Sliders[0].Pin);
        Assert.Equal(35u, vm.Draft.Sliders[1].Pin);
    }

    [Fact]
    public void TheSetupChangedNotice_GoesWhenTheDraftIsCleanAgain()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.SetSliderPin(1, 34);   // dirty

        var changed = ControllerPresets.ApplyTo(new ControllerConfig(), Diy);
        changed.Sliders[0].Pin = 39;
        var blob = ControllerConfigCodec.Encode(changed);
        var save = _rig.Runtime.ControllerConfig.SaveAsync(blob, TestContext.Current.CancellationToken);
        ConfirmLastWrite(blob);
        Until(() => save.IsCompleted && vm.BaselineConfig().Sliders[0].Pin == 39);
        Assert.NotNull(vm.Notice);

        vm.SetSliderPin(0, 39);   // the draft now equals the controller's new setup
        vm.SetSliderPin(1, 35);
        Assert.False(vm.IsDirty);
        Assert.Null(vm.Notice);
    }

    // ── Button timing (slider-feel spec, "The save rule") ────────────────────────────────

    /// <summary>Review Focus 5: the sample interval has no control, and a value set elsewhere survives a save.</summary>
    [Fact]
    public void SliderInterval_HiddenAndKept()
    {
        var config = ControllerPresets.ApplyTo(new ControllerConfig(), Diy);
        (config.Input ??= new InputTuning()).SliderIntervalMs = 20;
        var mirror = ControllerConfigCodec.Encode(config);
        var vm = Vm(mirror);
        GreetInSync(mirror);
        vm.SmoothingPct = 10;   // High
        vm.Deadband = 40;
        vm.SaveCommand.Execute(null);
        Until(() => Conn.SentFrames.Any(f => f.Length > 3 && f[2] == 0x88));
        var sent = ControllerConfigCodec.Encode(vm.Draft);
        ConfirmLastWrite(sent);
        Until(() => !vm.IsSaving);

        var saved = ControllerConfigCodec.Decode(Convert.FromBase64String(_rig.Config.Config.ControllerConfigBlob!));
        Assert.Equal(20u, saved.Input.SliderIntervalMs);
        Assert.Equal(10u, saved.Input.FilterWeightPct);
    }

    [Fact]
    public void TimingAndController_Save_CommitsTimingOnConfirmation()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.HoldMs = 700;
        vm.DebounceMs = 45;
        vm.SaveCommand.Execute(null);
        Assert.True(vm.IsSaving);
        Until(() => Conn.SentFrames.Any(f => f.Length > 3 && f[2] == 0x88));
        Assert.Equal(500, _rig.Config.Config.ButtonTiming.LongPressMs);   // not before the controller confirms

        ConfirmLastWrite(ControllerConfigCodec.Encode(vm.Draft));
        Until(() => !vm.IsSaving);
        Assert.Equal(700, _rig.Config.Config.ButtonTiming.LongPressMs);
        Assert.Equal(700m, vm.HoldMs);
        Assert.False(vm.TimingDirty);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void TimingAndController_FailedSave_KeepsTimingUnsaved()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.HoldMs = 700;
        vm.DebounceMs = 45;
        vm.SaveCommand.Execute(null);
        Until(() => Conn.SentFrames.Any(f => f.Length > 3 && f[2] == 0x88));
        Conn.RaiseMessage(new ConfigResultMessage(LastWriteBurst()[3], ConfigResultCode.Invalid, 0, 2));
        Until(() => !vm.IsSaving);
        Assert.Equal(500, _rig.Config.Config.ButtonTiming.LongPressMs);
        Assert.Equal(700m, vm.HoldMs);
        Assert.True(vm.TimingDirty);
        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public void Discard_RevertsTiming()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.HoldMs = 700;
        vm.DoublePressMs = 250;
        Assert.True(vm.IsDirty);
        Assert.True(vm.HasUnsavedChanges);
        Assert.True(vm.CanSave);

        vm.RevertCommand.Execute(null);
        Assert.Equal(500m, vm.HoldMs);
        Assert.Equal(300m, vm.DoublePressMs);
        Assert.False(vm.IsDirty);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void NotInSync_TimingNotEditable()
    {
        var vm = Vm(DiyBlob);
        vm.HoldMs = 700;
        Assert.Equal(500m, vm.HoldMs);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void ANullTiming_IsIgnored()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.HoldMs = null;
        Assert.Equal(500m, vm.HoldMs);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void MirrorChange_WithOnlyTimingDirty_DraftFollows_TimingKept()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.HoldMs = 700;

        var changed = ControllerPresets.ApplyTo(new ControllerConfig(), Diy);
        changed.Sliders[0].Pin = 39;
        var blob = ControllerConfigCodec.Encode(changed);
        var save = _rig.Runtime.ControllerConfig.SaveAsync(blob, TestContext.Current.CancellationToken);
        ConfirmLastWrite(blob);
        Until(() => save.IsCompleted && vm.BaselineConfig().Sliders[0].Pin == 39);
        Assert.Equal(blob, ControllerConfigCodec.Encode(vm.Draft));   // the controller draft followed
        Assert.Null(vm.Notice);
        Assert.Equal(700m, vm.HoldMs);
        Assert.True(vm.TimingDirty);
        Assert.False(vm.ControllerDirty);
    }

    [Fact]
    public void ANewDraft_KeepsTheTimingDraft()
    {
        var vm = Vm();
        Greet(Hello(ControllerConfigState.None, 0));
        vm.StartFromScratchCommand.Execute(null);
        vm.HoldMs = 700;
        vm.StartFromPresetCommand.Execute(Diy);
        Assert.Equal(700m, vm.HoldMs);
        Assert.True(vm.TimingDirty);
    }

    // ── Presets ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void APreset_KeepsBehaviourAndWifi_AndAsksOnlyWhenTheDraftHasHardware()
    {
        var vm = Vm();
        Greet(Hello(ControllerConfigState.None, 0));
        vm.StartFromScratchCommand.Execute(null);
        vm.Brightness = 40;
        vm.UdpEnabled = true;

        vm.LoadPreset(Diy);                            // a blank draft: no question
        Assert.Null(vm.Confirmation);
        Assert.Equal(5, vm.Draft.Sliders.Count);
        Assert.Equal(40m, vm.Brightness);
        Assert.True(vm.UdpEnabled);

        vm.LoadPreset(Blank);                          // replaces hardware: asks, not in red
        Assert.Equal("Replace the current hardware with Blank?", vm.Confirmation!.Message);
        Assert.False(vm.Confirmation.IsDestructive);
        vm.Confirmation.CancelCommand.Execute(null);
        Assert.Equal(5, vm.Draft.Sliders.Count);

        vm.LoadPreset(Blank);
        vm.Confirmation!.ConfirmCommand.Execute(null);
        Assert.Empty(vm.Draft.Sliders);
        Assert.Null(vm.Confirmation);
    }

    // ── Errors ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ALocalViolation_IsAProblemOfItsField()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.SetSliderPin(0, 6);
        var problem = Assert.Single(vm.Problems);
        Assert.Equal((ConfigField.SliderPin, 0), (problem.Field, (int)problem.Item));
        Assert.Equal("Pin 6 is reserved on the ESP32 (USB serial or flash).", problem.Message);

        vm.SaveCommand.Execute(null);
        Assert.Equal("Fix the problem marked in red first.", vm.ProgressText);
        Assert.Equal(LinkTone.Danger, vm.ProgressTone);
        Assert.Equal(vm.ProgressText, vm.Failure);   // the card shows it
        Assert.DoesNotContain(Conn.SentFrames, f => f.Length > 3 && f[2] == 0x88);
    }

    [Fact]
    public void AnInvalidResult_IsAProblemOfItsField_AndTheCardsError()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.Brightness = 50;
        vm.SaveCommand.Execute(null);
        Until(() => Conn.SentFrames.Any(f => f.Length > 3 && f[2] == 0x88));
        Conn.RaiseMessage(new ConfigResultMessage(LastWriteBurst()[3], ConfigResultCode.Invalid, 0, 2));
        Until(() => !vm.IsSaving);
        Assert.Equal("The controller rejected the setup: the button pin on Button 1.", vm.ProgressText);
        Assert.Equal(vm.ProgressText, vm.Failure);   // a failure stays on the card until the next action
        var problem = Assert.Single(vm.Problems);
        Assert.Equal((ConfigField.ButtonPin, 0), (problem.Field, (int)problem.Item));
        Assert.Equal("The controller didn't accept the button pin.", problem.Message);
    }

    // ── Controls ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RemovingAControl_AsksFirst_InRed()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.AskRemoveSlider(0);
        Assert.Equal("Remove Slider 1?", vm.Confirmation!.Message);
        Assert.True(vm.Confirmation.IsDestructive);
        vm.Confirmation.CancelCommand.Execute(null);
        Assert.Equal(5, vm.Draft.Sliders.Count);

        vm.AskRemoveSlider(4);                         // the last one asks too
        vm.Confirmation!.ConfirmCommand.Execute(null);
        Assert.Equal(4, vm.Draft.Sliders.Count);
        Assert.False(vm.Layout.IsIdentity);

        vm.AskRemoveButton(0);
        vm.Confirmation!.ConfirmCommand.Execute(null);
        Assert.Equal(2, vm.Draft.Buttons.Count);
    }

    [Fact]
    public void AScreenButton_BringsTheScreenSettings()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        Assert.False(vm.ShowBehaviour);
        vm.Draft.Buttons[0].Screen = new Screen { Bus = 2, Address = 0x3C };
        vm.OnDraftEdited();
        Assert.True(vm.ShowBehaviour);
    }

    // ── Save ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Save_Confirmed_ShowsSaved_AndIsClean()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.SetSliderPin(0, 39);
        vm.SaveCommand.Execute(null);
        Assert.Equal("Sending…", vm.ProgressText);
        var expected = ControllerConfigCodec.Encode(vm.Draft);
        ConfirmLastWrite(expected);
        Until(() => !vm.IsSaving);
        Assert.Equal("Saved. The controller restarted with the new setup.", vm.ProgressText);
        Assert.Equal(LinkTone.Good, vm.ProgressTone);
        Assert.Null(vm.Failure);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void AFailedSave_StaysUntilTheNextEdit()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.SetSliderPin(0, 6);
        vm.SaveCommand.Execute(null);
        Assert.Equal("Fix the problem marked in red first.", vm.Failure);

        vm.SetSliderPin(0, 39);
        Assert.Null(vm.ProgressText);
        Assert.Null(vm.Failure);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void APinConflict_IsAProblemOfBothEnds()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.SetSliderPin(0, 39);
        vm.SetSliderPin(1, 39);

        Assert.Equal(new[]
        {
            (0, "Pin 39 is also used by Slider 2. Each pin can only do one thing."),
            (1, "Pin 39 is also used by Slider 1. Each pin can only do one thing."),
        }, vm.Problems.Select(p => ((int)p.Item, p.Message)).OrderBy(p => p.Item1).ToArray());
        Assert.All(vm.Problems, p => Assert.Equal(ConfigField.SliderPin, p.Field));
    }

    [Fact]
    public void Save_DisabledDuringTestModeAndOperations()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.SetSliderPin(0, 39);
        Assert.True(vm.CanSave);

        vm.StartTestCommand.Execute(null);
        Assert.False(vm.CanSave);
        vm.StopTestCommand.Execute(null);
        Assert.True(vm.CanSave);

        var read = _rig.Runtime.ControllerConfig.ReadAsync(adopt: false, TestContext.Current.CancellationToken);
        _ui.RunAll();
        Assert.False(vm.CanSave);
        Assert.False(vm.CanStartTest);
        _rig.Runtime.Reconnect();   // ends the read
        Until(() => read.IsCompleted);
    }

    /// <summary>A valid Wi-Fi setup. The DIY sliders on 32-36 are ADC1, so Wi-Fi doesn't block them.</summary>
    private static void EnableWifi(ControllerViewModel vm)
    {
        vm.UdpEnabled = true;
        vm.Ssid = "home";
        vm.Password = "password1";
        vm.PcIp = "192.168.1.10";
        Assert.Null(vm.WifiError);
    }

    [Fact]
    public void ChangingWifiOverAUdpConnection_AsksFirst()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        _rig.Config.Config.Connection.Type = ConnectionType.Udp;   // not saved: no restart
        EnableWifi(vm);
        vm.SaveCommand.Execute(null);
        Assert.StartsWith("Changing the Wi-Fi settings may disconnect the controller.", vm.Confirmation!.Message);
        Assert.False(vm.Confirmation.IsDestructive);   // a warning, not in red
        Assert.DoesNotContain(Conn.SentFrames, f => f.Length > 3 && f[2] == 0x88);
        vm.Confirmation.ConfirmCommand.Execute(null);
        Until(() => Conn.SentFrames.Any(f => f.Length > 3 && f[2] == 0x88));
    }

    [Fact]
    public void ChangingWifiOverSerial_DoesNotAsk()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        EnableWifi(vm);
        Assert.Equal((decimal)_rig.Config.Config.Connection.Udp.ListenPort, vm.PcPort);   // pre-filled
        vm.SaveCommand.Execute(null);
        Assert.Null(vm.Confirmation);
        Until(() => Conn.SentFrames.Any(f => f.Length > 3 && f[2] == 0x88));
    }

    [Fact]
    public void ApplyMine_SendsTheMirrorBytesUnchanged()
    {
        var mirror = new byte[] { 0x98, 0x06, 0x01 }.Concat(DiyBlob).ToArray();
        var vm = Vm(mirror);
        Greet(Hello(ControllerConfigState.Stored, 0x1234));
        vm.ApplyMineCommand.Execute(null);
        Until(() => Conn.SentFrames.Any(f => f.Length > 3 && f[2] == 0x88));
        var burst = LastWriteBurst();
        var chunk = burst.Skip(burst[1] + 3).ToArray();          // the frame after Begin
        Assert.Equal(0x89, chunk[2]);
        Assert.Equal(mirror, chunk[6..(2 + chunk[1])]);
    }

    [Fact]
    public void FixIt_IsDirty_AndShowsTheLocalError()
    {
        var broken = ControllerPresets.ApplyTo(new ControllerConfig(), Diy);
        broken.Sliders[0].Pin = 6;
        var mirror = ControllerConfigCodec.Encode(broken);
        var vm = Vm(mirror);
        Greet(Hello(ControllerConfigState.Damaged, ControllerConfigCodec.Crc32(mirror)));
        Assert.True(vm.Banner.Actions.HasFlag(BannerActions.FixIt));
        vm.FixItCommand.Execute(null);
        Assert.True(vm.CanEdit);
        Assert.True(vm.IsDirty);
        Assert.Contains(vm.Problems, p => p.Field == ConfigField.SliderPin && p.Item == 0);
    }

    [Fact]
    public void Revert_ClosesAPendingQuestion_AndResetsTheIpBox()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.AddSliderCommand.Execute(null);
        vm.PcIp = "192.168.1.5";
        vm.AskRemoveSlider(4);                       // asks
        var stale = vm.Confirmation!;

        vm.RevertCommand.Execute(null);
        Assert.Null(vm.Confirmation);
        Assert.Equal("", vm.PcIp);
        stale.ConfirmCommand.Execute(null);          // a stale button does nothing
        Assert.Equal(5, vm.Draft.Sliders.Count);
    }

    [Fact]
    public void LosingEditing_ClosesAPendingQuestion()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.AskRemoveSlider(0);
        Assert.NotNull(vm.Confirmation);
        Conn.RaiseMessage(Hello(ControllerConfigState.Stored, 0x9999));   // a different setup now
        _ui.RunAll();
        Assert.False(vm.CanEdit);
        Assert.Null(vm.Confirmation);
    }

    [Fact]
    public void AnUnencodableDraft_SaysSo()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.UdpEnabled = true;
        vm.Ssid = new string('x', 40);   // over the limit; the box caps it, this is the model path
        vm.UdpEnabled = false;           // the validator skips Wi-Fi now
        vm.SaveCommand.Execute(null);
        Assert.StartsWith("This setup can't be saved:", vm.ProgressText);
        Assert.DoesNotContain(Conn.SentFrames, f => f.Length > 3 && f[2] == 0x88);
    }

    [Fact]
    public void AnInputTuningError_IsAProblem()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.DebounceMs = 0;               // below 1
        Assert.Contains(vm.Problems, p => p.Field == ConfigField.InputValue);
    }

    // ── Test mode ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TestMode_Indicators_AndLeavingThePage()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.StartTestCommand.Execute(null);
        Assert.True(vm.IsTesting);
        Assert.True(_rig.Runtime.InTestMode);
        Assert.False(vm.CanEdit);                      // editing is paused

        Conn.RaiseMessage(new ButtonMessage(2, ButtonState.Pressed));
        Conn.RaiseMessage(new ButtonMessage(0, ButtonState.Pressed));
        _ui.RunAll();
        Assert.Contains(Conn.SentFrames, f => f.AsSpan().SequenceEqual(ProtocolEncoder.EncodeLed(2, 1)));   // the pair moved to A
        Assert.Contains(Conn.SentFrames, f => f.AsSpan().SequenceEqual(ProtocolEncoder.EncodeLed(0, 1)));
        Conn.RaiseMessage(new ButtonMessage(0, ButtonState.Released));
        Conn.RaiseMessage(new ButtonMessage(2, ButtonState.Released));
        Conn.RaiseMessage(new ButtonMessage(2, ButtonState.Pressed));
        _ui.RunAll();
        Assert.Contains(Conn.SentFrames, f => f.AsSpan().SequenceEqual(ProtocolEncoder.EncodeLed(2, 2)));

        vm.StopTestCommand.Execute(null);
        Assert.False(_rig.Runtime.InTestMode);
        Assert.True(vm.CanEdit);
        vm.StartTestCommand.Execute(null);

        vm.OnPageLeft();
        Assert.False(_rig.Runtime.InTestMode);
        Assert.False(vm.IsTesting);
    }

    [Fact]
    public void TestMode_EndedByTheRuntime_ClearsIsTesting()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.StartTestCommand.Execute(null);
        Conn.SetStatus(TransportStatus.Closed);
        _ui.RunAll();
        Assert.False(vm.IsTesting);
        Assert.False(_rig.Runtime.InTestMode);
    }

    [Fact]
    public void TheNavDot_StartsFromTheBanner()
    {
        using var settings = _rig.CreateSettings(_ui);
        Assert.False(settings.ControllerNavDot);   // no bundled firmware
    }

    [Fact]
    public void ClearingANumberBox_RestoresTheDefault_AndSavesTheFieldAsAbsent()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.Brightness = 40;
        Assert.True(vm.Draft.Screens.HasBrightness);
        vm.Brightness = null;                       // the user empties the box
        Assert.Equal(255m, vm.Brightness);          // the proto default shows again
        Assert.False(vm.Draft.Screens.HasBrightness);
        vm.DebounceMs = null;
        Assert.Equal(30m, vm.DebounceMs);
        vm.PcPort = null;
        Assert.Equal(12345m, vm.PcPort);
    }
}
