using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Controller;
using Xunit;
using static Volco.Core.Tests.Controller.ConfigServiceKit;

namespace Volco.Core.Tests.Controller;

/// <summary>Spec §8.3, every row of the table and the rules under it.</summary>
public class ControllerBannerTests
{
    private static readonly byte[] Mirror = Small;
    private static readonly uint MirrorCrc = ControllerConfigCodec.Crc32(Small);
    private static readonly byte[] Undecodable = { 0xFF, 0xFF, 0xFF };

    private static ControllerBanner Compute(HelloMessage? hello, byte[]? mirror = null, bool readFailed = false,
        ConfigOperation op = ConfigOperation.Idle, ConnectionType type = ConnectionType.Serial, string? bundled = null)
        => ControllerBannerLogic.Compute(new BannerInputs(hello, mirror, op, readFailed, type, bundled));

    private static HelloMessage Stored(uint crc) => Hello(ControllerConfigState.Stored, crc);

    [Fact]
    public void NoHello_NoBanner() => Assert.Equal(ControllerBanner.Empty, Compute(null, Mirror));

    [Fact]
    public void Stored_SameCrc_InSync()
        => Assert.Equal(BannerKind.None, Compute(Stored(MirrorCrc), Mirror).Kind);

    [Fact]
    public void Stored_DifferentCrc_DifferentSetup()
    {
        var b = Compute(Stored(0x1234), Mirror);
        Assert.Equal(BannerKind.DifferentSetup, b.Kind);
        Assert.Equal(BannerActions.UseControllers | BannerActions.ApplyMine, b.Actions);
    }

    [Fact]
    public void Stored_DifferentCrc_FailedRead_OffersRetry()
    {
        var b = Compute(Stored(0x1234), Mirror, readFailed: true);
        Assert.Equal(BannerKind.ReadFailed, b.Kind);
        Assert.Equal(BannerActions.UseControllers | BannerActions.ApplyMine | BannerActions.Retry, b.Actions);
    }

    [Fact]
    public void Stored_NoMirror_NoBanner_WhileTheAutomaticReadRuns()
        => Assert.Equal(BannerKind.None, Compute(Stored(0x1234)).Kind);

    [Fact]
    public void Stored_NoMirror_FailedRead()
    {
        var b = Compute(Stored(0x1234), readFailed: true);
        Assert.Equal(BannerKind.ReadFailed, b.Kind);
        Assert.Equal(BannerActions.Retry | BannerActions.SetUpFromScratch, b.Actions);
    }

    [Fact]
    public void None_WithMirror_NotSetUp_ApplySaved()
    {
        var b = Compute(Hello(ControllerConfigState.None, 0), Mirror);
        Assert.Equal(BannerKind.NotSetUpHasMirror, b.Kind);
        Assert.Equal(BannerActions.ApplySaved | BannerActions.SetUpFromScratch, b.Actions);
    }

    [Fact]
    public void None_NoMirror_SetUp()
    {
        var b = Compute(Hello(ControllerConfigState.None, 0));
        Assert.Equal(BannerKind.NotSetUp, b.Kind);
        Assert.Equal(BannerActions.SetUp, b.Actions);
    }

    [Fact]
    public void Damaged_DifferentCrc_Unusable()
    {
        var b = Compute(Hello(ControllerConfigState.Damaged, 0x1234), Mirror);
        Assert.Equal(BannerKind.Unusable, b.Kind);
        Assert.Equal(BannerActions.ApplySaved | BannerActions.SetUpFromScratch, b.Actions);
    }

    [Fact]
    public void Damaged_NoMirror_Unusable_SetUpFromScratchOnly()
    {
        var b = Compute(Hello(ControllerConfigState.Damaged, 0x1234));
        Assert.Equal(BannerKind.Unusable, b.Kind);
        Assert.Equal(BannerActions.SetUpFromScratch, b.Actions);
    }

    [Fact]
    public void Damaged_SameCrc_BrokenWithThisFirmware_FixIt()
    {
        var b = Compute(Hello(ControllerConfigState.Damaged, MirrorCrc), Mirror);
        Assert.Equal(BannerKind.BrokenWithFirmware, b.Kind);
        Assert.Equal(BannerActions.FixIt, b.Actions);
    }

    [Fact]
    public void VersionOnlyHello_Incompatible_BeforeAnyConfigStateRow()
    {
        var b = Compute(HelloMessage.VersionOnly(2), Mirror, bundled: "abc123");
        Assert.Equal(BannerKind.Incompatible, b.Kind);
        Assert.Equal(BannerActions.Update, b.Actions);   // offered although Chip is Unknown
        Assert.True(b.FirmwareUpdateAvailable);
    }

    [Theory]
    [InlineData(ConnectionType.WebSocket, "abc123")]
    [InlineData(ConnectionType.Udp, "abc123")]
    [InlineData(ConnectionType.Serial, null)]
    public void VersionOnlyHello_NoUpdate_OffSerialOrWithoutFirmware(ConnectionType type, string? bundled)
    {
        var b = Compute(HelloMessage.VersionOnly(2), type: type, bundled: bundled);
        Assert.Equal(BannerKind.Incompatible, b.Kind);
        Assert.Equal(BannerActions.None, b.Actions);
        Assert.False(b.FirmwareUpdateAvailable);
    }

    [Fact]
    public void AnUndecodableMirror_CountsAsAbsent()
    {
        Assert.Equal(BannerKind.NotSetUp, Compute(Hello(ControllerConfigState.None, 0), Undecodable).Kind);
        Assert.Equal(BannerKind.None, Compute(Stored(ControllerConfigCodec.Crc32(Undecodable)), Undecodable).Kind);
        Assert.Equal(BannerActions.SetUpFromScratch,
                     Compute(Hello(ControllerConfigState.Damaged, 1), Undecodable).Actions);
    }

    [Fact]
    public void AnOperation_ShowsInsteadOfTheTable()
    {
        Assert.Equal(BannerKind.Saving, Compute(Stored(0x1234), Mirror, op: ConfigOperation.Writing).Kind);
        Assert.Equal(BannerKind.Reading, Compute(Stored(0x1234), op: ConfigOperation.Reading).Kind);
    }

    [Theory]
    [InlineData(ConnectionType.Serial, "rel-1", "rel-2", true)]
    [InlineData(ConnectionType.Serial, "rel-2", "rel-2", false)]     // the bundled build: no notice
    [InlineData(ConnectionType.Serial, "dev", "rel-2", false)]       // development firmware is never offered
    [InlineData(ConnectionType.Serial, "rel-1", null, false)]        // no catalog
    [InlineData(ConnectionType.WebSocket, "rel-1", "rel-2", false)]  // the simulator
    [InlineData(ConnectionType.Udp, "rel-1", "rel-2", false)]
    public void TheFirmwareNotice(ConnectionType type, string fw, string? bundled, bool expected)
    {
        var b = Compute(Hello(ControllerConfigState.Stored, MirrorCrc, fw), Mirror, type: type, bundled: bundled);
        Assert.Equal(expected, b.FirmwareUpdateAvailable);
        Assert.Equal(fw == "dev", b.DevelopmentFirmware);
    }

    [Fact]
    public void TheFirmwareNotice_NeedsAnEsp32()
    {
        var other = new HelloMessage(ProtocolVersion.Current, ChipFamily.Unknown, "rel-1", ControllerConfigState.Stored,
                                     MirrorCrc, 0, Array.Empty<IndicatorKind>());
        Assert.False(Compute(other, Mirror, bundled: "rel-2").FirmwareUpdateAvailable);
    }
}
