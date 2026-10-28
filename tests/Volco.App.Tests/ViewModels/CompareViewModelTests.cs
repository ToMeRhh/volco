using System.Diagnostics;
using Volco.App.ViewModels;
using Volco.App.ViewModels.Hardware;
using Volco.Core.Communication;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Volco.Core.Tests.Runtime;
using Xunit;

namespace Volco.App.Tests.ViewModels;

/// <summary>Hardware-page spec §7.2: Compare reads the controller's setup without adopting it,
/// shows only what differs, and Keep or Send settles it.</summary>
public sealed class CompareViewModelTests : IDisposable
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

    /// <summary>The controller's setup: the DIY one with Slider 2 moved.</summary>
    private static readonly uint MovedPin = ControllerConfigCodec.Decode(DiyBlob).Sliders[1].Pin == 35 ? 39u : 35u;
    private static readonly byte[] TheirBlob = Moved();

    private static byte[] Moved()
    {
        var c = ControllerConfigCodec.Decode(DiyBlob);
        c.Sliders[1].Pin = MovedPin;
        return ControllerConfigCodec.Encode(c);
    }

    private FakeControllerConnection Conn => _rig.Connection!;
    private HardwareViewModel Hardware => _settings!.Hardware;

    /// <summary>Volco's copy is the DIY setup; the controller holds <see cref="TheirBlob"/>.</summary>
    private void DifferentSetup()
    {
        _rig.Config.Config.ControllerConfigBlob = Convert.ToBase64String(DiyBlob);
        _settings = _rig.CreateSettings(_ui);
        _ = _settings.Hardware;
        Conn.SetStatus(TransportStatus.Open);
        Conn.RaiseMessage(new HelloMessage(ProtocolVersion.Current, ChipFamily.Esp32, "dev", ControllerConfigState.Stored,
                                           ControllerConfigCodec.Crc32(TheirBlob), 5, DiyKinds));
        _ui.RunAll();
        AnswerRead(1, TheirBlob);   // the automatic look for a Wi-Fi fallback: more differs, so nothing is adopted
        // The banner stays None during the look and becomes DifferentSetup once it ends.
        Until(() => _rig.Runtime.ControllerConfig.Operation == ConfigOperation.Idle
                    && _settings.Controller.Banner.Kind == BannerKind.DifferentSetup);
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

    private int Reads() => Conn.SentFrames.Count(f => f.Length > 3 && f[2] == 0x87);

    private bool AnyWrite() => Conn.SentFrames.Any(f => f.Length > 3 && f[2] == 0x88);

    /// <summary>Waits for read number <paramref name="nth"/> and answers it with <paramref name="blob"/>.</summary>
    private void AnswerRead(int nth, byte[] blob)
    {
        Until(() => Reads() >= nth);
        var seq = Conn.SentFrames.Last(f => f.Length > 3 && f[2] == 0x87)[3];
        Conn.RaiseMessage(new ConfigDataMessage(seq, (ushort)blob.Length, 0, ControllerConfigCodec.Crc32(blob), blob));
    }

    /// <summary>Opens Compare and answers its read with the controller's setup.</summary>
    private CompareViewModel OpenAndRead()
    {
        var before = Reads();
        Hardware.OpenCompare();
        Assert.True(Hardware.IsCompareOpen);
        var compare = Hardware.Compare!;
        Assert.Equal("The controller has a different setup", compare.Title);
        AnswerRead(before + 1, TheirBlob);
        Until(() => compare.HasComparison);
        return compare;
    }

    [Fact]
    public void OpeningReadsWithoutAdopting_AndListsOnlyTheDifferences()
    {
        DifferentSetup();
        var compare = OpenAndRead();
        Assert.Equal(new[] { $"Slider 2 on GPIO {MovedPin}" }, compare.ControllerLines);
        Assert.Equal(new[] { $"Slider 2 on GPIO {ControllerConfigCodec.Decode(DiyBlob).Sliders[1].Pin}" }, compare.VolcoLines);
        Assert.Equal(TheirBlob, ControllerConfigCodec.Encode(compare.ControllerPicture!));
        Assert.NotNull(compare.VolcoPicture);
        Assert.Equal(DiyBlob, _rig.Runtime.ControllerConfig.Mirror);   // not adopted
        Assert.False(AnyWrite());
    }

    [Fact]
    public void KeepAdoptsWithoutWriting()
    {
        DifferentSetup();
        var compare = OpenAndRead();
        var before = Reads();
        compare.KeepCommand.Execute(null);
        AnswerRead(before + 1, TheirBlob);
        Until(() => !Hardware.IsCompareOpen);

        Assert.Equal(TheirBlob, _rig.Runtime.ControllerConfig.Mirror);
        Assert.False(AnyWrite());
        Assert.Null(Hardware.Compare);
    }

    [Fact]
    public void SendWritesTheMirrorBytes()
    {
        DifferentSetup();
        var compare = OpenAndRead();
        compare.SendCommand.Execute(null);
        Assert.False(Hardware.IsCompareOpen);

        Until(AnyWrite);
        var burst = Conn.SentFrames.Last(f => f.Length > 3 && f[2] == 0x88);
        var chunk = burst.Skip(burst[1] + 3).ToArray();          // the frame after Begin
        Assert.Equal(0x89, chunk[2]);
        Assert.Equal(DiyBlob, chunk[6..(2 + chunk[1])]);
    }

    [Fact]
    public void AFailedReadOffersRetry()
    {
        DifferentSetup();
        var before = Reads();
        Hardware.OpenCompare();
        var compare = Hardware.Compare!;
        AnswerRead(before + 1, new byte[] { 0xFF, 0xFF, 0xFF });   // bytes this app can't decode
        Until(() => compare.HasError);
        Assert.Equal("Couldn't read the controller's setup", compare.Error);
        Assert.False(compare.HasComparison);

        compare.RetryCommand.Execute(null);
        AnswerRead(before + 2, TheirBlob);
        Until(() => compare.HasComparison);
        Assert.False(compare.HasError);
    }
}
