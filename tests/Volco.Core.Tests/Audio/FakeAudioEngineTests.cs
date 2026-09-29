using Volco.Core.Audio;
using Xunit;

namespace Volco.Core.Tests.Audio;

public class FakeAudioEngineTests
{
    [Fact]
    public void RecordsDeviceVolumeCalls()
    {
        using var fake = new FakeAudioEngine();
        fake.SetDeviceVolume(DeviceRef.ById("dev-1"), 0.25f);

        var (device, level) = Assert.Single(fake.DeviceVolumeCalls);
        Assert.Equal(DeviceRef.ById("dev-1"), device);
        Assert.Equal(0.25f, level, 3);
    }

    [Fact]
    public void RecordsSessionVolumeCalls()
    {
        using var fake = new FakeAudioEngine();
        fake.SetSessionVolume(new SessionFilter.ByImageNames(new[] { "spotify.exe" }), 0.5f);

        var (filter, level) = Assert.Single(fake.SessionVolumeCalls);
        var byName = Assert.IsType<SessionFilter.ByImageNames>(filter);
        Assert.Equal(new[] { "spotify.exe" }, byName.Names);
        Assert.Equal(0.5f, level, 3);
    }

    [Fact]
    public void RaiseSessionCreated_FiresSessionsChanged()
    {
        using var fake = new FakeAudioEngine();
        SessionsChangedEventArgs? seen = null;
        fake.SessionsChanged += (_, e) => seen = e;

        var session = new AudioSessionInfo("inst-1", 42, "spotify.exe", "dev-1", false);
        fake.RaiseSessionCreated(session);

        Assert.NotNull(seen);
        Assert.Equal(SessionChangeKind.Created, seen!.Kind);
        Assert.Equal("spotify.exe", seen.Session.ImageName);
    }

    [Fact]
    public void TryGetDeviceState_ReturnsSeededState()
    {
        using var fake = new FakeAudioEngine();
        fake.DeviceStates["dev-1"] = (0.4f, true);

        Assert.True(fake.TryGetDeviceState(DeviceRef.ById("dev-1"), out var level, out var muted));
        Assert.Equal(0.4f, level, 3);
        Assert.True(muted);
    }

    [Fact]
    public void TryGetDeviceState_UnknownDevice_ReturnsFalse()
    {
        using var fake = new FakeAudioEngine();
        Assert.False(fake.TryGetDeviceState(DeviceRef.ById("nope"), out _, out _));
    }
}
