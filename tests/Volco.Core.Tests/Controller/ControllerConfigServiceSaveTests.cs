using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Controller;
using Xunit;
using static Volco.Core.Tests.Controller.ConfigServiceKit;

namespace Volco.Core.Tests.Controller;

/// <summary>Spec §5.4, the app write rules, one test per §12.3 save bullet.</summary>
public sealed class ControllerConfigServiceSaveTests : IDisposable
{
    private readonly ConfigServiceKit _k = new();
    private static readonly uint SmallCrc = ControllerConfigCodec.Crc32(Small);
    private static readonly HelloMessage Confirming = Hello(ControllerConfigState.Stored, SmallCrc);

    public void Dispose() => _k.Dispose();

    private string? Mirror => _k.Config.Config.ControllerConfigBlob;

    [Fact]
    public async Task TheBurst_IsTheEncodedWrite()
    {
        var save = _k.Service.SaveAsync(Small);
        Assert.Equal(ProtocolEncoder.EncodeConfigWrite(_k.LastSeq, Small), _k.Link.Bursts.Single());
        _k.Service.OnConnectionReplaced();
        await save;
    }

    [Fact]
    public async Task Ok_ThenAConfirmingHello_Confirms_AndUpdatesTheMirror()
    {
        var mirrorChanges = 0;
        _k.Service.MirrorChanged += (_, _) => mirrorChanges++;
        var save = _k.Service.SaveAsync(Small);
        await _k.Step(() => _k.Service.OnMessage(Result(_k.LastSeq, ConfigResultCode.Ok)));
        Assert.Equal(SavePhase.Confirming, _k.Service.Progress!.Phase);
        _k.Service.OnMessage(Confirming);
        Assert.Equal(SaveStatus.Confirmed, (await save).Status);
        Assert.Equal(Convert.ToBase64String(Small), Mirror);
        Assert.Equal(1, mirrorChanges);
        Assert.Equal(ConfigOperation.Idle, _k.Service.Operation);
    }

    [Fact]
    public async Task Ok_ThenAHelloWithTheOldCrc_IsNotRunningTheNewSetup()
    {
        var save = _k.Service.SaveAsync(Small);
        await _k.Step(() => _k.Service.OnMessage(Result(_k.LastSeq, ConfigResultCode.Ok)));
        _k.Service.OnMessage(Hello(ControllerConfigState.Stored, 0xDEAD));
        Assert.Equal(SaveStatus.NotRunningNewSetup, (await save).Status);
        Assert.Null(Mirror);
    }

    [Theory]
    [InlineData(ConnectionType.Serial, 10)]
    [InlineData(ConnectionType.WebSocket, 10)]
    [InlineData(ConnectionType.Udp, 20)]
    public async Task Ok_ThenNoHello_DidNotComeBack_AfterTheConfirmingTimeout(ConnectionType type, int seconds)
    {
        _k.Link.ConnectionType = type;
        var save = _k.Service.SaveAsync(Small);
        await _k.Step(() => _k.Service.OnMessage(Result(_k.LastSeq, ConfigResultCode.Ok)));
        _k.Time.Advance(TimeSpan.FromSeconds(seconds - 0.1));
        await Settle();
        Assert.False(save.IsCompleted);
        _k.Time.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal(SaveStatus.DidNotComeBack, (await save).Status);
        Assert.Null(Mirror);
    }

    [Fact]
    public async Task BadCrc_ThenOk_TwoAttempts_AndTheFirstAttemptsLateResultIsIgnored()
    {
        var save = _k.Service.SaveAsync(Small);
        var first = _k.LastSeq;
        _k.Service.OnMessage(Result(first, ConfigResultCode.BadCrc));
        await Until(() => _k.Link.Bursts.Count == 2);
        var second = _k.LastSeq;
        Assert.NotEqual(first, second);

        await _k.Step(() => _k.Service.OnMessage(Result(first, ConfigResultCode.Ok)));   // leftover: ignored
        Assert.Equal(new SaveProgress(SavePhase.Sending, 2), _k.Service.Progress);

        await _k.Step(() => _k.Service.OnMessage(Result(second, ConfigResultCode.Ok)));
        _k.Service.OnMessage(Confirming);
        Assert.Equal(SaveStatus.Confirmed, (await save).Status);
    }

    [Fact]
    public async Task BadSequence_StartsTheNextAttempt()
    {
        var save = _k.Service.SaveAsync(Small);
        _k.Service.OnMessage(Result(_k.LastSeq, ConfigResultCode.BadSequence));
        await Until(() => _k.Link.Bursts.Count == 2);
        _k.Service.OnMessage(Confirming);
        Assert.Equal(SaveStatus.Confirmed, (await save).Status);
    }

    [Fact]
    public async Task OkLost_ThenANewCrcHello_Confirms()
    {
        var save = _k.Service.SaveAsync(Small);
        _k.Service.OnMessage(Confirming);   // implied OK while still waiting for the result
        Assert.Equal(SaveStatus.Confirmed, (await save).Status);
        Assert.Single(_k.Link.Bursts);
    }

    [Fact]
    public async Task AnUnexpectedHelloBeforeTheResult_StartsTheNextAttempt()
    {
        var save = _k.Service.SaveAsync(Small);
        _k.Service.OnMessage(Hello(ControllerConfigState.None, 0));   // reset by hand
        await Until(() => _k.Link.Bursts.Count == 2);
        _k.Service.OnMessage(Confirming);
        Assert.Equal(SaveStatus.Confirmed, (await save).Status);
    }

    [Theory]
    [InlineData(ConfigResultCode.Invalid, 2, 7)]
    [InlineData(ConfigResultCode.DecodeFailed, 0xFF, 0xFF)]
    [InlineData(ConfigResultCode.TooLarge, 0xFF, 0xFF)]
    [InlineData(ConfigResultCode.StorageFailed, 0xFF, 0xFF)]
    public async Task ARejection_IsNotRetried_AndCarriesItsCode(ConfigResultCode code, byte item, byte field)
    {
        var save = _k.Service.SaveAsync(Small);
        _k.Service.OnMessage(Result(_k.LastSeq, code, item, field));
        var r = await save;
        Assert.Equal(new ConfigSaveResult(SaveStatus.Rejected, code, item, field), r);
        Assert.Single(_k.Link.Bursts);
        Assert.Null(Mirror);
    }

    private async Task ThreeUnansweredAttempts()
    {
        await _k.Step(() => _k.Time.Advance(TimeSpan.FromSeconds(3)));
        await _k.Step(() => _k.Time.Advance(TimeSpan.FromSeconds(3)));
        await _k.Step(() => _k.Time.Advance(TimeSpan.FromSeconds(3)));
        Assert.Equal(SavePhase.WaitingForImpliedOk, _k.Service.Progress!.Phase);
    }

    [Fact]
    public async Task NoResultThreeTimes_ThenNothing_NoAnswer()
    {
        var save = _k.Service.SaveAsync(Small);
        await ThreeUnansweredAttempts();
        Assert.Equal(3, _k.Link.Bursts.Count);
        _k.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(SaveStatus.NoAnswer, (await save).Status);
    }

    [Fact]
    public async Task NoResultThreeTimes_ThenANewCrcHello_Confirms()
    {
        var save = _k.Service.SaveAsync(Small);
        await ThreeUnansweredAttempts();
        await _k.Step(() => _k.Service.OnMessage(Hello(ControllerConfigState.Stored, 0xDEAD)));   // not ours: keep waiting
        _k.Service.OnMessage(Confirming);
        Assert.Equal(SaveStatus.Confirmed, (await save).Status);
    }

    [Fact]
    public async Task SendRawFalse_CountsAsAnUnansweredAttempt()
    {
        _k.Link.Attached = false;   // e.g. UDP detached during the reboot
        var save = _k.Service.SaveAsync(Small);
        Assert.Equal(1, _k.Link.Calls);
        await _k.Step(() => _k.Time.Advance(TimeSpan.FromSeconds(3)));
        Assert.Equal(2, _k.Link.Calls);
        _k.Service.OnMessage(Confirming);
        Assert.Equal(SaveStatus.Confirmed, (await save).Status);
    }

    [Fact]
    public async Task ConnectionReplacedMidSave_ConnectionLost_MirrorUnchanged()
    {
        _k.SetMirror(new byte[] { 0x42, 0x01, 0x78 });
        var before = Mirror;
        var save = _k.Service.SaveAsync(Small);
        await _k.Step(() => _k.Service.OnMessage(Result(_k.LastSeq, ConfigResultCode.Ok)));
        _k.Service.OnConnectionReplaced();
        Assert.Equal(SaveStatus.ConnectionLost, (await save).Status);
        Assert.Equal(before, Mirror);
    }

    [Fact]
    public async Task ASecondRequestWhileBusy_IsRejected()
    {
        var save = _k.Service.SaveAsync(Small);
        Assert.Equal(SaveStatus.Busy, (await _k.Service.SaveAsync(Small)).Status);
        Assert.Equal(ReadStatus.Busy, (await _k.Service.ReadAsync(adopt: true)).Status);
        _k.Service.OnMessage(Confirming);
        Assert.Equal(SaveStatus.Confirmed, (await save).Status);
    }

    [Fact]
    public async Task CancelledBeforeTheBurst_SendsNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(SaveStatus.Cancelled, (await _k.Service.SaveAsync(Small, cts.Token)).Status);
        Assert.Equal(0, _k.Link.Calls);
        Assert.Equal(ConfigOperation.Idle, _k.Service.Operation);
    }

    [Fact]
    public async Task CancelledAfterTheBurst_TheSaveCarriesOn()
    {
        using var cts = new CancellationTokenSource();
        var save = _k.Service.SaveAsync(Small, cts.Token);
        cts.Cancel();
        await _k.Step(() => _k.Service.OnMessage(Result(_k.LastSeq, ConfigResultCode.Ok)));
        _k.Service.OnMessage(Confirming);
        Assert.Equal(SaveStatus.Confirmed, (await save).Status);
    }

    [Fact]
    public async Task DuringASave_TheBannerShowsTheOperation()
    {
        _k.Service.OnMessage(Hello(ControllerConfigState.None, 0));
        Assert.Equal(BannerKind.NotSetUp, _k.Service.Banner.Kind);
        var save = _k.Service.SaveAsync(Small);
        Assert.Equal(BannerKind.Saving, _k.Service.Banner.Kind);
        _k.Service.OnMessage(Confirming);
        await save;
        await Until(() => _k.Service.Banner.Kind == BannerKind.None);   // in sync with the new mirror
    }
}
