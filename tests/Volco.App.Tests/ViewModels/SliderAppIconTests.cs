using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Volco.App.Platform;
using Volco.App.ViewModels;
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.App.Tests.ViewModels;

/// <summary>An apps slider shows its app's icon (<see cref="AppIconLookup"/>): the first of its apps
/// with a live session that has an icon, kept once seen.</summary>
public sealed class SliderAppIconTests : IDisposable
{
    private const string SpotifyIcon = @"C:\Apps\Spotify.exe";

    private readonly TestRig _rig;
    private readonly FakeAppIcons _icons = new();
    private readonly AppIconLookup _lookup;

    public SliderAppIconTests()
    {
        var speakers = new AudioDevice("dev-1", "Speakers", AudioFlow.Render, true);
        _rig = new TestRig(new FakeAudioEngine { Devices = { speakers }, DefaultRender = speakers });
        _lookup = new AppIconLookup(_rig.Engine, _icons);
        Config.Sliders[0].Type = SliderMappingType.Processes;
        Config.Sliders[0].Processes = new List<string> { "discord.exe", "spotify.exe" };
        Config.Sliders[1].Type = SliderMappingType.Device;
    }

    public void Dispose()
    {
        _lookup.Dispose();
        _rig.Dispose();
    }

    private AppConfig Config => _rig.Config.Config;

    private static AudioSessionInfo Spotify(uint pid = 1)
        => new($"s-{pid}", pid, "Spotify.exe", "dev-1", false, "Spotify", SpotifyIcon);

    private SlidersViewModel Sliders() => new(_rig.Config, _rig.Engine, new UndoToastViewModel(), _lookup);

    [AvaloniaFact]
    public void AppsSlider_ShowsTheFirstAppWithAnIcon_DeviceSliderNone()
    {
        _rig.Engine.Sessions.Add(new AudioSessionInfo("s-9", 9, "discord.exe", "dev-1", false));   // no icon path
        _rig.Engine.Sessions.Add(Spotify());
        using var sliders = Sliders();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(_icons.Of(SpotifyIcon), sliders.Items[0].AppIcon);
        Assert.True(sliders.Items[0].HasAppIcon);
        Assert.Null(sliders.Items[1].AppIcon);
        Assert.Equal(new[] { SpotifyIcon }, _icons.Requests);
    }

    [AvaloniaFact]
    public void AppNotRunning_NoIcon_UntilItStarts()
    {
        using var sliders = Sliders();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(sliders.Items[0].AppIcon);

        var session = Spotify();
        _rig.Engine.Sessions.Add(session);
        _rig.Engine.RaiseSessionCreated(session);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(sliders.Items[0].AppIcon);
    }

    [AvaloniaFact]
    public void Icon_KeptAfterTheAppQuits_AndMovesWithASwap()
    {
        var session = Spotify();
        _rig.Engine.Sessions.Add(session);
        using var sliders = Sliders();
        Dispatcher.UIThread.RunJobs();
        _rig.Engine.Sessions.Clear();
        _rig.Engine.RaiseSessionExpired(session);
        Dispatcher.UIThread.RunJobs();

        sliders.Swap(0, 1);

        Assert.Null(sliders.Items[0].AppIcon);
        Assert.Same(_icons.Of(SpotifyIcon), sliders.Items[1].AppIcon);
        Assert.Single(_icons.Requests);
    }

    [AvaloniaFact]
    public void Dispose_StopsFollowingTheLookup()
    {
        var sliders = Sliders();
        sliders.Dispose();
        var session = Spotify();
        _rig.Engine.Sessions.Add(session);
        _rig.Engine.RaiseSessionCreated(session);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(sliders.Items[0].AppIcon);
    }
}
