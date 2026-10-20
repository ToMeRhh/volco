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

/// <summary>Spec §8.4 and §12.4: the Controller page's editing model, against a real runtime over
/// a fake connection.</summary>
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
        Assert.Equal(5, vm.Sliders.Count);
        Assert.False(vm.Sliders[0].CanEdit);
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
        Assert.True(vm.ShowUseControllers);
        Assert.True(vm.ShowApplyMine);
    }

    [Fact]
    public void SetUp_GrantsEditing_UntilADifferentHello()
    {
        var vm = Vm();
        Greet(Hello(ControllerConfigState.None, 0));
        Assert.Equal("This controller isn't set up yet.", vm.BannerText);
        Assert.False(vm.CanEdit);
        vm.SetUpCommand.Execute(null);
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
    public void ANotSetUpController_OpensNoDialog_TheBannerOffersSetUp()
    {
        var vm = Vm();
        Greet(Hello(ControllerConfigState.None, 0));
        Assert.False(vm.HasDialog);                    // the Hardware page's chooser replaces the dialog
        Assert.True(vm.ShowSetUp);
        Assert.False(vm.CanEdit);
    }

    [Fact]
    public void SetupFromAPreset_StartsADraftThatMustBeSaved()
    {
        var vm = Vm();
        Greet(Hello(ControllerConfigState.None, 0));
        Assert.Equal(Diy, vm.SetupPresets[0]);
        vm.StartFromPresetCommand.Execute(Diy);
        Assert.True(vm.CanEdit);
        Assert.Equal(5, vm.Sliders.Count);
        Assert.True(vm.IsSettingUp);
        Assert.True(vm.CanSave);
        Assert.Equal("This setup isn't on the controller yet.", vm.FooterText);
    }

    [Fact]
    public void SettingUp_ADraftEqualToVolcosSavedCopy_IsStillUnsaved_UntilTheControllerHasIt()
    {
        // An erased controller, and Volco still has the same setup saved: the draft equals the
        // mirror, but the controller doesn't hold it, so Save to controller must stay.
        var vm = Vm(DiyBlob);
        Greet(Hello(ControllerConfigState.None, 0));
        Assert.Equal(BannerKind.NotSetUpHasMirror, vm.Banner.Kind);
        Assert.True(vm.ShowApplySaved);
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
    public void SetupApplySaved_StartsADraftOfTheMirror_SentBySave()
    {
        var vm = Vm(DiyBlob);
        Greet(Hello(ControllerConfigState.None, 0));
        vm.SetupApplySavedCommand.Execute(null);
        Assert.DoesNotContain(Conn.SentFrames, f => f.Length > 3 && f[2] == 0x88);   // nothing is sent before Save
        Assert.Equal(DiyBlob, ControllerConfigCodec.Encode(vm.Draft));
        Assert.Equal("This setup isn't on the controller yet.", vm.FooterText);
        vm.SaveCommand.Execute(null);
        Until(() => Conn.SentFrames.Any(f => f.Length > 3 && f[2] == 0x88));
    }

    // ── Dirty ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AMirrorWithUnknownFields_IsNotDirty_AndAPinChangeIs()
    {
        var mirror = new byte[] { 0x98, 0x06, 0x01 }.Concat(DiyBlob).ToArray();   // field 99, unknown to this app
        var vm = Vm(mirror);
        GreetInSync(mirror);
        Assert.False(vm.IsDirty);

        vm.Sliders[0].Pin = 39;
        Assert.True(vm.IsDirty);
        Assert.True(vm.CanSave);
        vm.Sliders[0].Pin = 33;
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void AMirrorChange_RebasesACleanDraft_KeepsADirtyOneWithTheNotice()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);

        var changed = ControllerPresets.ApplyTo(new ControllerConfig(), Diy);
        changed.Sliders[0].Pin = 39;
        var blob1 = ControllerConfigCodec.Encode(changed);
        var save1 = _rig.Runtime.ControllerConfig.SaveAsync(blob1, TestContext.Current.CancellationToken);
        ConfirmLastWrite(blob1);
        Until(() => save1.IsCompleted && vm.Sliders.Count == 5 && vm.Sliders[0].Pin == 39);
        Assert.False(vm.IsDirty);
        Assert.Null(vm.Notice);

        vm.Sliders[1].Pin = 34;   // dirty now
        changed.Sliders[0].Pin = 33;
        var blob2 = ControllerConfigCodec.Encode(changed);
        var save2 = _rig.Runtime.ControllerConfig.SaveAsync(blob2, TestContext.Current.CancellationToken);
        ConfirmLastWrite(blob2);
        Until(() => save2.IsCompleted && vm.Notice is not null);
        Assert.Equal("The controller's setup changed. Revert to load it.", vm.Notice);
        Assert.Equal(34m, vm.Sliders[1].Pin);

        vm.RevertCommand.Execute(null);
        Assert.Null(vm.Notice);
        Assert.Equal(33m, vm.Sliders[0].Pin);
        Assert.Equal(35m, vm.Sliders[1].Pin);
    }

    // ── Presets ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void APreset_KeepsBehaviourAndWifi_AndAsksOnlyWhenTheDraftHasHardware()
    {
        var vm = Vm();
        Greet(Hello(ControllerConfigState.None, 0));
        vm.SetUpCommand.Execute(null);
        vm.Brightness = 40;
        vm.UdpEnabled = true;

        vm.SelectedPreset = Diy;                       // a blank draft: no question
        Assert.Null(vm.Confirmation);
        Assert.Equal(5, vm.Sliders.Count);
        Assert.Equal(40m, vm.Brightness);
        Assert.True(vm.UdpEnabled);
        Assert.Equal("Based on DIY reference", vm.BasedOnText);

        vm.SelectedPreset = Blank;                     // replaces hardware: asks
        Assert.Equal("Replace the current hardware with Blank?", vm.Confirmation!.Message);
        vm.Confirmation.CancelCommand.Execute(null);
        Assert.Equal(5, vm.Sliders.Count);

        vm.SelectedPreset = Blank;
        vm.Confirmation!.ConfirmCommand.Execute(null);
        Assert.Empty(vm.Sliders);
        Assert.Null(vm.Confirmation);
    }

    [Fact]
    public void TheModifiedCaption()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        Assert.Equal("Based on DIY reference", vm.BasedOnText);
        vm.Buttons[0].Pin = 13;
        Assert.Equal("Based on DIY reference (modified)", vm.BasedOnText);
    }

    // ── Errors ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ALocalViolation_IsShownOnItsRow()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.Sliders[0].Pin = 6;
        Assert.NotNull(vm.Sliders[0].ErrorText);
        Assert.All(vm.Sliders.Skip(1), r => Assert.Null(r.ErrorText));

        Assert.Equal("Pin 6 is reserved on the ESP32 (USB serial or flash).", vm.Sliders[0].ErrorText);
        Assert.True(vm.Sliders[0].PinInvalid);

        vm.SaveCommand.Execute(null);
        Assert.Equal("Fix the problem marked in red first.", vm.ProgressText);
        Assert.Equal(LinkTone.Danger, vm.FooterTone);
        Assert.DoesNotContain(Conn.SentFrames, f => f.Length > 3 && f[2] == 0x88);
    }

    [Fact]
    public void AnInvalidResult_IsShownOnItsRow_WithTheFooterMessage()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.Brightness = 50;
        vm.SaveCommand.Execute(null);
        Until(() => Conn.SentFrames.Any(f => f.Length > 3 && f[2] == 0x88));
        Conn.RaiseMessage(new ConfigResultMessage(LastWriteBurst()[3], ConfigResultCode.Invalid, 0, 2));
        Until(() => !vm.IsSaving);
        Assert.Equal("The controller rejected the setup: the button pin on Button 1.", vm.ProgressText);
        Assert.Equal("The controller didn't accept the button pin.", vm.Buttons[0].ErrorText);
        Assert.True(vm.CanDismissFooter);   // a failure stays until closed
    }

    [Fact]
    public void Hints_AppearOnTheirRows()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.Buttons[0].Pin = 35;
        Assert.Equal("This pin has no internal pull-up; add a resistor to 3.3 V.", vm.Buttons[0].HintText);
        vm.Sliders[0].Pin = 25;
        Assert.Equal("This pin can't be read if you enable Wi-Fi.", vm.Sliders[0].HintText);
    }

    // ── Rows ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RemovingARow_AsksFirst()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.Sliders[0].RemoveCommand.Execute(null);
        Assert.Equal("Remove Slider 1?", vm.Confirmation!.Message);
        vm.Confirmation.CancelCommand.Execute(null);
        Assert.Equal(5, vm.Sliders.Count);

        vm.Sliders[4].RemoveCommand.Execute(null);     // the last one asks too
        vm.Confirmation!.ConfirmCommand.Execute(null);
        Assert.Equal(4, vm.Sliders.Count);
        Assert.False(vm.Layout.IsIdentity);

        vm.Buttons[0].RemoveCommand.Execute(null);
        vm.Confirmation!.ConfirmCommand.Execute(null);
        Assert.Equal(2, vm.Buttons.Count);
        Assert.Equal("Button 1", vm.Buttons[0].Title);
    }

    [Fact]
    public void AScreenButton_ShowsItsBusRow_AndBehaviour()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        Assert.False(vm.ShowBehaviour);
        Assert.False(vm.HasBuses);
        vm.Buttons[0].IndicatorIndex = 3;
        vm.Buttons[0].ScreenBusIndex = 2;
        Assert.True(vm.ShowBehaviour);
        Assert.Equal(new[] { 2 }, vm.Buses.Select(b => b.Index));
    }

    // ── Save ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Save_Confirmed_ShowsSaved_AndIsClean()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.Sliders[0].Pin = 39;
        vm.SaveCommand.Execute(null);
        Assert.Equal("Sending…", vm.ProgressText);
        var expected = ControllerConfigCodec.Encode(vm.Draft);
        ConfirmLastWrite(expected);
        Until(() => !vm.IsSaving);
        Assert.Equal("Saved. The controller restarted with the new setup.", vm.ProgressText);
        Assert.Equal(LinkTone.Good, vm.FooterTone);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void ASavedMessage_GoesByItself_AFailureStaysUntilClosed()
    {
        var vm = Vm(DiyBlob);
        vm.ResultTimeout = TimeSpan.FromMilliseconds(50);
        GreetInSync(DiyBlob);
        vm.Sliders[0].Pin = 39;
        vm.SaveCommand.Execute(null);
        ConfirmLastWrite(ControllerConfigCodec.Encode(vm.Draft));
        Until(() => vm.ProgressText == "Saved. The controller restarted with the new setup.");
        Until(() => vm.ProgressText is null);
        Assert.False(vm.ShowFooter);

        vm.Sliders[0].Pin = 6;
        vm.SaveCommand.Execute(null);
        Thread.Sleep(150);
        _ui.RunAll();
        Assert.Equal("Fix the problem marked in red first.", vm.ProgressText);
        vm.DismissFooterCommand.Execute(null);
        Assert.Null(vm.ProgressText);
        Assert.Equal("Unsaved changes", vm.FooterText);   // the draft is still unsaved
        Assert.True(vm.ShowFooter);
    }

    [Fact]
    public void AnEdit_SaysUnsavedChanges()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        Assert.Null(vm.FooterText);
        vm.Sliders[0].Pin = 39;
        Assert.Equal("Unsaved changes", vm.FooterText);
        Assert.False(vm.CanDismissFooter);
    }

    [Fact]
    public void APinConflict_MarksBothRows()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.Sliders[0].Pin = 39;
        vm.Sliders[1].Pin = 39;

        Assert.Equal("Pin 39 is also used by Slider 2. Each pin can only do one thing.", vm.Sliders[0].ErrorText);
        Assert.Equal("Pin 39 is also used by Slider 1. Each pin can only do one thing.", vm.Sliders[1].ErrorText);
        Assert.True(vm.Sliders[0].PinInvalid);
        Assert.True(vm.Sliders[1].PinInvalid);
        Assert.All(vm.Sliders.Skip(2), r => Assert.False(r.HasError));
        Assert.All(vm.Buttons, r => Assert.False(r.HasError));
    }

    [Fact]
    public void Save_DisabledDuringTestModeAndOperations()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.Sliders[0].Pin = 39;
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
        Assert.True(vm.ShowFixIt);
        vm.FixItCommand.Execute(null);
        Assert.True(vm.CanEdit);
        Assert.True(vm.IsDirty);
        Assert.NotNull(vm.Sliders[0].ErrorText);
    }

    [Fact]
    public void Revert_ClosesAPendingQuestion_AndResetsTheIpBox()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.AddSliderCommand.Execute(null);
        vm.PcIp = "192.168.1.5";
        vm.Sliders[4].RemoveCommand.Execute(null);   // asks
        var stale = vm.Confirmation!;

        vm.RevertCommand.Execute(null);
        Assert.Null(vm.Confirmation);
        Assert.Equal("", vm.PcIp);
        stale.ConfirmCommand.Execute(null);          // a stale button does nothing
        Assert.Equal(5, vm.Sliders.Count);
    }

    [Fact]
    public void LosingEditing_ClosesAPendingQuestion()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.Sliders[0].RemoveCommand.Execute(null);
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
    public void AnInputTuningError_OpensAdvanced()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        Assert.False(vm.IsAdvancedOpen);
        vm.DebounceMs = 0;               // below 1
        Assert.NotNull(vm.InputError);
        Assert.True(vm.IsAdvancedOpen);
    }

    // ── Test mode ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TestMode_LiveInput_Indicators_AndLeavingThePage()
    {
        var vm = Vm(DiyBlob);
        GreetInSync(DiyBlob);
        vm.StartTestCommand.Execute(null);
        Assert.True(vm.IsTesting);
        Assert.True(_rig.Runtime.InTestMode);
        Assert.Equal(5, vm.TestSliders.Count);

        Conn.RaiseMessage(new SliderMessage(1, 2000));
        Conn.RaiseMessage(new ButtonMessage(2, ButtonState.Pressed));
        _ui.RunAll();
        Assert.Equal(2000, vm.TestSliders[1].Value);
        Assert.True(vm.TestButtons[2].IsPressed);

        Assert.Equal("LED A", vm.TestButtons[2].StateText);   // a press moved the pair to A
        Assert.True(vm.HasDialog);

        Conn.RaiseMessage(new ButtonMessage(0, ButtonState.Pressed));
        _ui.RunAll();
        Assert.Equal(1, vm.TestButtons[0].Presses);
        Assert.Equal("LED on", vm.TestButtons[0].StateText);
        Assert.Contains(Conn.SentFrames, f => f.AsSpan().SequenceEqual(ProtocolEncoder.EncodeLed(0, 1)));
        Conn.RaiseMessage(new ButtonMessage(0, ButtonState.Released));
        Conn.RaiseMessage(new ButtonMessage(2, ButtonState.Released));
        Conn.RaiseMessage(new ButtonMessage(2, ButtonState.Pressed));
        _ui.RunAll();
        Assert.Equal("LED B", vm.TestButtons[2].StateText);
        Assert.Contains(Conn.SentFrames, f => f.AsSpan().SequenceEqual(ProtocolEncoder.EncodeLed(2, 2)));

        vm.CloseDialogCommand.Execute(null);   // Escape or a click outside the dialog
        Assert.False(_rig.Runtime.InTestMode);
        Assert.Equal(0, vm.TestButtons[0].Presses);
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
