using System.Diagnostics;
using Volco.Core.Communication;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Volco.Core.Firmware;
using Volco.Core.Tests.Firmware;
using Xunit;

namespace Volco.App.Tests.Shell;

public class ExitGateTests
{
    [Fact]
    public void AnExitDuringAWrite_RunsOnceTheWriteEnds_ExactlyOnce()
    {
        var busy = true;
        var exits = 0;
        var gate = new ExitGate(() => busy);
        Assert.True(gate.ShouldDefer());
        gate.Defer(() => exits++);
        gate.Defer(() => exits++);           // a second Exit click: still one exit
        gate.OnBusyChanged();
        Assert.Equal(0, exits);
        busy = false;
        gate.OnBusyChanged();
        gate.OnBusyChanged();
        Assert.Equal(1, exits);
    }

    [Fact]
    public void NotBusy_NothingIsDeferred() => Assert.False(new ExitGate(() => false).ShouldDefer());

    /// <summary>The run's gate also holds an Exit (and the updater's restart) while a config save is
    /// in flight, until the controller confirms it.</summary>
    [Fact]
    public void AnExitDuringAConfigSave_RunsOnceTheSaveEnds()
    {
        using var rig = new TestRig();
        var service = rig.Runtime.ControllerConfig;
        var firmware = new FirmwareService(rig.Runtime, new FakeFlasher(() => rig.Runtime.IsSuspended), FirmwareCatalog.Empty);
        var gate = ExitGate.For(firmware, service, a => a());
        var blob = ControllerConfigCodec.Encode(new ControllerConfig { Preset = ControllerPresets.BlankId });
        var conn = rig.Connection!;
        conn.SetStatus(TransportStatus.Open);
        conn.RaiseMessage(new HelloMessage(ProtocolVersion.Current, ChipFamily.Esp32, "dev", ControllerConfigState.None, 0, 0,
                                           Array.Empty<IndicatorKind>()));
        Assert.False(gate.ShouldDefer());

        var exits = 0;
        var save = service.SaveAsync(blob);
        Assert.Equal(ConfigOperation.Writing, service.Operation);
        Assert.True(gate.ShouldDefer());
        gate.Defer(() => exits++);

        Until(() => conn.SentFrames.Any(f => f.Length > 3 && f[2] == 0x88));
        var burst = conn.SentFrames.Last(f => f.Length > 3 && f[2] == 0x88);
        conn.RaiseMessage(new ConfigResultMessage(burst[3], ConfigResultCode.Ok, 0xFF, 0xFF));
        Assert.Equal(0, exits);   // restarting: still held
        conn.RaiseMessage(new HelloMessage(ProtocolVersion.Current, ChipFamily.Esp32, "dev", ControllerConfigState.Stored,
                                           ControllerConfigCodec.Crc32(blob), 0, Array.Empty<IndicatorKind>()));
        Assert.True(save.Wait(5000));
        Assert.Equal(SaveStatus.Confirmed, save.Result.Status);
        Until(() => exits == 1);
        Assert.False(gate.ShouldDefer());
    }

    private static void Until(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > 5000) throw new TimeoutException();
            Thread.Sleep(5);
        }
    }
}
