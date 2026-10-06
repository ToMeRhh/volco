using Volco.Core.Communication;
using Volco.Core.Controller;
using Xunit;
using static Volco.Core.Tests.Controller.ConfigServiceKit;

namespace Volco.Core.Tests.Controller;

/// <summary>Spec §5.3 and the automatic read of §8.3.</summary>
public sealed class ControllerConfigServiceReadTests : IDisposable
{
    private readonly ConfigServiceKit _k = new();

    public void Dispose() => _k.Dispose();

    [Fact]
    public async Task SingleChunk_Assembles()
    {
        var read = _k.Service.ReadAsync(adopt: false);
        _k.Deliver(Chunks(_k.LastSeq, Small));
        var r = await read;
        Assert.Equal(ReadStatus.Ok, r.Status);
        Assert.Equal(Small, r.Blob);
        Assert.Single(_k.Link.Bursts);
        Assert.Equal(ProtocolEncoder.EncodeConfigRead(_k.LastSeq), _k.Link.Bursts[0]);
    }

    [Fact]
    public async Task ThreeChunks_Assemble()
    {
        var blob = Opaque(600);
        var read = _k.Service.ReadAsync(adopt: false);
        _k.Deliver(Chunks(_k.LastSeq, blob));
        var r = await read;
        Assert.Equal(ReadStatus.Ok, r.Status);
        Assert.Equal(blob, r.Blob);
    }

    [Fact]
    public async Task StaleSeqFrames_AreIgnored()
    {
        var read = _k.Service.ReadAsync(adopt: false);
        var seq = _k.LastSeq;
        _k.Deliver(Chunks((byte)(seq - 1), Opaque(300)));
        _k.Deliver(Chunks(seq, Small));
        var r = await read;
        Assert.Equal(Small, r.Blob);
        Assert.Single(_k.Link.Bursts);
    }

    [Fact]
    public async Task Gap_StartsANewAttemptWithANewSeq()
    {
        var blob = Opaque(600);
        var read = _k.Service.ReadAsync(adopt: false);
        var first = _k.LastSeq;
        _k.Service.OnMessage(Chunks(first, blob).Skip(1).First());   // offset 240 before 0
        await Until(() => _k.Link.Bursts.Count == 2);
        Assert.NotEqual(first, _k.LastSeq);
        _k.Deliver(Chunks(_k.LastSeq, blob));
        Assert.Equal(blob, (await read).Blob);
    }

    [Fact]
    public async Task CrcMismatch_StartsANewAttempt()
    {
        var read = _k.Service.ReadAsync(adopt: false);
        _k.Deliver(Chunks(_k.LastSeq, Small, crc: 0x12345678));
        await Until(() => _k.Link.Bursts.Count == 2);
        _k.Deliver(Chunks(_k.LastSeq, Small));
        Assert.Equal(ReadStatus.Ok, (await read).Status);
    }

    [Fact]
    public async Task ThreeTimeouts_Fail_AfterThreeReads()
    {
        var read = _k.Service.ReadAsync(adopt: false);
        await _k.Step(() => _k.Time.Advance(TimeSpan.FromSeconds(2)));
        await _k.Step(() => _k.Time.Advance(TimeSpan.FromSeconds(2)));
        _k.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(ReadStatus.Failed, (await read).Status);
        Assert.Equal(3, _k.Link.Bursts.Count);
        Assert.Equal(3, _k.Link.Bursts.Select(SeqOf).Distinct().Count());
    }

    [Fact]
    public async Task TheTimeout_RestartsOnEachAcceptedChunk()
    {
        var blob = Opaque(600);
        var read = _k.Service.ReadAsync(adopt: false);
        var chunks = Chunks(_k.LastSeq, blob).ToList();
        await _k.Step(() => _k.Service.OnMessage(chunks[0]));
        _k.Time.Advance(TimeSpan.FromSeconds(1.5));
        await _k.Step(() => _k.Service.OnMessage(chunks[1]));
        _k.Time.Advance(TimeSpan.FromSeconds(1.5));   // 3 s after the ConfigRead, 1.5 s after the last chunk
        _k.Service.OnMessage(chunks[2]);
        Assert.Equal(ReadStatus.Ok, (await read).Status);
        Assert.Single(_k.Link.Bursts);
    }

    [Fact]
    public async Task EmptyConfig_IsOk_AndNeverAdopted()
    {
        var mirrorChanges = 0;
        _k.Service.MirrorChanged += (_, _) => mirrorChanges++;
        var read = _k.Service.ReadAsync(adopt: true);
        _k.Deliver(Chunks(_k.LastSeq, Array.Empty<byte>()));
        var r = await read;
        Assert.Equal(ReadStatus.Ok, r.Status);
        Assert.Empty(r.Blob!);
        Assert.Null(_k.Config.Config.ControllerConfigBlob);
        Assert.Equal(0, mirrorChanges);
    }

    [Fact]
    public async Task Adopt_WritesTheMirror_AndRaisesMirrorChangedOnce()
    {
        var mirrorChanges = 0;
        _k.Service.MirrorChanged += (_, _) => mirrorChanges++;
        var read = _k.Service.ReadAsync(adopt: true);
        _k.Deliver(Chunks(_k.LastSeq, Small));
        await read;
        Assert.Equal(Convert.ToBase64String(Small), _k.Config.Config.ControllerConfigBlob);
        Assert.Equal(1, mirrorChanges);
    }

    [Fact]
    public async Task WithoutAdopt_TheMirrorIsUntouched()
    {
        var read = _k.Service.ReadAsync(adopt: false);
        _k.Deliver(Chunks(_k.LastSeq, Small));
        await read;
        Assert.Null(_k.Config.Config.ControllerConfigBlob);
    }

    [Fact]
    public async Task AHelloEqualToTheStartingOne_IsIgnored_ADifferentOneCancels()
    {
        _k.SetMirror(Small);                                   // in "different setup": no automatic read
        var start = Hello(ControllerConfigState.Stored, 0xAAAA);
        _k.Service.OnMessage(start);
        Assert.Equal(ConfigOperation.Idle, _k.Service.Operation);

        var read = _k.Service.ReadAsync(adopt: false);
        await _k.Step(() => _k.Service.OnMessage(Hello(ControllerConfigState.Stored, 0xAAAA)));
        Assert.False(read.IsCompleted);
        _k.Service.OnMessage(Hello(ControllerConfigState.Stored, 0xBBBB));
        Assert.Equal(ReadStatus.Cancelled, (await read).Status);
    }

    [Fact]
    public async Task ASecondRequestWhileBusy_IsRejected()
    {
        var first = _k.Service.ReadAsync(adopt: false);
        Assert.Equal(ReadStatus.Busy, (await _k.Service.ReadAsync(adopt: false)).Status);
        Assert.Equal(SaveStatus.Busy, (await _k.Service.SaveAsync(Small)).Status);
        _k.Deliver(Chunks(_k.LastSeq, Small));
        Assert.Equal(ReadStatus.Ok, (await first).Status);
    }

    [Fact]
    public async Task ConnectionReplaced_Cancels()
    {
        var read = _k.Service.ReadAsync(adopt: false);
        _k.Service.OnConnectionReplaced();
        Assert.Equal(ReadStatus.Cancelled, (await read).Status);
        Assert.Null(_k.Service.LastHello);
    }

    // ── The automatic read (spec §8.3) ──────────────────────────────────────────────────────

    [Fact]
    public async Task RepeatedIdenticalHellos_OneAutomaticRead_UnchangedBanner()
    {
        var bannerChanges = 0;
        _k.Service.BannerChanged += (_, _) => Interlocked.Increment(ref bannerChanges);
        _k.Service.OnMessage(Hello(ControllerConfigState.Stored, 0x1234));
        await Until(() => _k.Link.Bursts.Count == 1);
        var afterFirst = Volatile.Read(ref bannerChanges);
        Assert.Equal(BannerKind.Reading, _k.Service.Banner.Kind);

        _k.Service.OnMessage(Hello(ControllerConfigState.Stored, 0x1234));
        _k.Service.OnMessage(Hello(ControllerConfigState.Stored, 0x1234));
        await Settle();
        Assert.Single(_k.Link.Bursts);
        Assert.Equal(afterFirst, Volatile.Read(ref bannerChanges));
    }

    [Fact]
    public async Task AutomaticRead_AdoptsTheControllersSetup()
    {
        _k.Service.OnMessage(Hello(ControllerConfigState.Stored, ControllerConfigCodec.Crc32(Small)));
        await Until(() => _k.Link.Bursts.Count == 1);
        _k.Deliver(Chunks(_k.LastSeq, Small));
        await Until(() => _k.Service.Operation == ConfigOperation.Idle && _k.Config.Config.ControllerConfigBlob != null);
        Assert.Equal(Convert.ToBase64String(Small), _k.Config.Config.ControllerConfigBlob);
        await Until(() => _k.Service.Banner.Kind == BannerKind.None);
    }

    [Fact]
    public async Task FailedAutomaticRead_NotRetriedForTheSameHello_RetriedForADifferentOne()
    {
        _k.Service.OnMessage(Hello(ControllerConfigState.Stored, 0x1234));
        await Until(() => _k.Link.Bursts.Count == 1);
        await _k.Step(() => _k.Time.Advance(TimeSpan.FromSeconds(2)));
        await _k.Step(() => _k.Time.Advance(TimeSpan.FromSeconds(2)));
        _k.Time.Advance(TimeSpan.FromSeconds(2));
        await Until(() => _k.Service.Banner.Kind == BannerKind.ReadFailed);
        Assert.Equal(BannerActions.Retry | BannerActions.SetUpFromScratch, _k.Service.Banner.Actions);

        _k.Service.OnMessage(Hello(ControllerConfigState.Stored, 0x1234));
        await Settle();
        Assert.Equal(3, _k.Link.Bursts.Count);

        _k.Service.OnMessage(Hello(ControllerConfigState.Stored, 0x5678));
        await Until(() => _k.Link.Bursts.Count == 4);
    }

    private static readonly byte[] Undecodable = { 0xFF, 0xFF, 0xFF };

    [Fact]
    public async Task AnUndecodableRead_IsAFailure_AndNeverReplacesTheMirror()
    {
        _k.SetMirror(Small);
        var before = _k.Config.Config.ControllerConfigBlob;
        _k.Service.OnMessage(Hello(ControllerConfigState.Stored, ControllerConfigCodec.Crc32(Undecodable)));
        Assert.Equal(BannerKind.DifferentSetup, _k.Service.Banner.Kind);

        var read = _k.Service.ReadAsync(adopt: true);   // "Use the controller's"
        _k.Deliver(Chunks(_k.LastSeq, Undecodable));
        Assert.Equal(ReadStatus.Failed, (await read).Status);
        Assert.Equal(before, _k.Config.Config.ControllerConfigBlob);
        await Until(() => _k.Service.Banner.Kind == BannerKind.ReadFailed);
        Assert.True(_k.Service.Banner.Actions.HasFlag(BannerActions.Retry));
    }

    [Fact]
    public async Task AnUndecodableAutomaticRead_ShowsReadFailed_WithRetry()
    {
        _k.Service.OnMessage(Hello(ControllerConfigState.Stored, ControllerConfigCodec.Crc32(Undecodable)));
        await Until(() => _k.Link.Bursts.Count == 1);
        _k.Deliver(Chunks(_k.LastSeq, Undecodable));
        await Until(() => _k.Service.Banner.Kind == BannerKind.ReadFailed);
        Assert.Equal(BannerActions.Retry | BannerActions.SetUpFromScratch, _k.Service.Banner.Actions);
        Assert.Null(_k.Config.Config.ControllerConfigBlob);
    }

    [Fact]
    public async Task ACancelledRetry_KeepsTheFailedState()
    {
        _k.Service.OnMessage(Hello(ControllerConfigState.Stored, 0x1234));
        await Until(() => _k.Link.Bursts.Count == 1);
        _k.Deliver(Chunks(_k.LastSeq, Undecodable));
        await Until(() => _k.Service.Banner.Kind == BannerKind.ReadFailed);

        using var cts = new CancellationTokenSource();
        var retry = _k.Service.ReadAsync(adopt: true, cts.Token);
        cts.Cancel();
        Assert.Equal(ReadStatus.Cancelled, (await retry).Status);
        await Until(() => _k.Service.Operation == ConfigOperation.Idle);
        await Until(() => _k.Service.Banner.Kind == BannerKind.ReadFailed);
    }

    [Fact]
    public async Task RetryReadAsync_Adopts()
    {
        _k.Service.OnMessage(Hello(ControllerConfigState.Stored, ControllerConfigCodec.Crc32(Small)));
        await Until(() => _k.Link.Bursts.Count == 1);
        await _k.Step(() => _k.Time.Advance(TimeSpan.FromSeconds(2)));
        await _k.Step(() => _k.Time.Advance(TimeSpan.FromSeconds(2)));
        _k.Time.Advance(TimeSpan.FromSeconds(2));
        await Until(() => _k.Service.Banner.Kind == BannerKind.ReadFailed);

        var retry = _k.Service.RetryReadAsync();
        _k.Deliver(Chunks(_k.LastSeq, Small));
        Assert.Equal(ReadStatus.Ok, (await retry).Status);
        Assert.Equal(Convert.ToBase64String(Small), _k.Config.Config.ControllerConfigBlob);
        Assert.Equal(BannerKind.None, _k.Service.Banner.Kind);
    }
}
