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
    public void ExePath_SavedFromTheSession_IconShowsNextRunWhileNotRunning()
    {
        const string exe = @"C:\Apps\Spotify\Spotify.exe";
        _rig.Engine.Sessions.Add(Spotify() with { IconPath = exe, ExePath = exe });
        using (var first = new AppIconLookup(_rig.Engine, _icons, _rig.Config))
        using (var sliders = new SlidersViewModel(_rig.Config, _rig.Engine, new UndoToastViewModel(), first))
            Dispatcher.UIThread.RunJobs();
        Assert.Equal(exe, Config.AppPathFor("SPOTIFY.EXE"));
        Assert.Equal(exe, Config.Sliders[0].AppPaths["spotify.exe"]);   // on the slider that lists it
        Assert.Empty(Config.Sliders[1].AppPaths);

        // The next run: Spotify is not playing.
        _rig.Engine.Sessions.Clear();
        _icons.Requests.Clear();
        using var next = new AppIconLookup(_rig.Engine, _icons, _rig.Config);
        using var again = new SlidersViewModel(_rig.Config, _rig.Engine, new UndoToastViewModel(), next);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(_icons.Of(exe), again.Items[0].AppIcon);
        Assert.Equal(new[] { exe }, _icons.Requests);
    }

    [AvaloniaFact]
    public void SavedPath_MovesWithASwap_GoesWhenTheAppLeavesTheSlider()
    {
        const string exe = @"D:\Games\Game.exe";
        Config.Sliders[0].Processes = new List<string> { "game.exe", "chat.exe" };
        Config.Sliders[0].RememberAppPath("game.exe", exe);
        Config.Sliders[0].RememberAppPath("chat.exe", @"C:\Chat\chat.exe");
        using var lookup = new AppIconLookup(_rig.Engine, _icons, _rig.Config);
        using var sliders = new SlidersViewModel(_rig.Config, _rig.Engine, new UndoToastViewModel(), lookup);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(_icons.Of(exe), sliders.Items[0].AppIcon);   // not running: from the saved path

        sliders.Swap(0, 1);
        Assert.Equal(exe, Config.Sliders[1].AppPathFor("game.exe"));
        Assert.Empty(Config.Sliders[0].AppPaths);

        // Another slider takes chat.exe: its path leaves slider 2 with it, game.exe's stays.
        sliders.SetTarget(sliders.Items[3], new SliderConfig { Type = SliderMappingType.Processes, Processes = { "chat.exe" } });
        Assert.Equal(new[] { "game.exe" }, Config.Sliders[1].AppPaths.Keys);
        Assert.Null(Config.Sliders[3].AppPathFor("chat.exe"));   // the dialog had no path for it
    }

    [AvaloniaFact]
    public void AppMovedByAnUpdate_WhileRunning_PathAndIconFollowAtOnce()
    {
        const string v1 = @"C:\Foo\v1\Spotify.exe", v2 = @"C:\Foo\v2\Spotify.exe";
        _rig.Engine.Sessions.Add(Spotify() with { IconPath = v1, ExePath = v1 });
        using var lookup = new AppIconLookup(_rig.Engine, _icons, _rig.Config);
        using var sliders = new SlidersViewModel(_rig.Config, _rig.Engine, new UndoToastViewModel(), lookup);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(_icons.Of(v1), sliders.Items[0].AppIcon);

        // The update restarts the app from its new folder.
        var updated = Spotify(2) with { IconPath = v2, ExePath = v2 };
        _rig.Engine.Sessions.Clear();
        _rig.Engine.Sessions.Add(updated);
        _rig.Engine.RaiseSessionCreated(updated);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(v2, Config.Sliders[0].AppPathFor("spotify.exe"));
        Assert.Same(_icons.Of(v2), sliders.Items[0].AppIcon);
    }

    [AvaloniaFact]
    public void SessionOfAnAppOnNoSlider_SavesNothing()
    {
        using var lookup = new AppIconLookup(_rig.Engine, _icons, _rig.Config);
        var other = new AudioSessionInfo("s-7", 7, "chrome.exe", "dev-1", false, "Chrome", @"C:\Chrome\chrome.exe", @"C:\Chrome\chrome.exe");

        _rig.Engine.RaiseSessionCreated(other);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(Config.AppPathFor("chrome.exe"));
        Assert.All(Config.Sliders, s => Assert.Empty(s.AppPaths));
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
