using Microsoft.Extensions.Time.Testing;
using Volco.Core.Config;
using Volco.Core.Programs;
using Volco.Core.Tests.Fakes;
using Xunit;

namespace Volco.Core.Tests.Programs;

public class ProgramOpenerTests
{
    private const string Game = @"C:\Games\Doom\doom.exe";
    private readonly FakeProgramLauncher _launcher = new();
    private readonly FakeTimeProvider _time = new();
    private readonly Dictionary<string, string?> _shortcuts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ProgramOpener _opener;

    public ProgramOpenerTests()
    {
        _opener = new ProgramOpener(_launcher, _time, path => _shortcuts.TryGetValue(path, out var t) ? t : null);
    }

    private static CommandConfig Cmd(string path) => new() { Path = path };

    [Fact]
    public void Running_IsBroughtToFront_NotStarted()
    {
        _launcher.SetRunning(Game, true);
        _opener.Open(Cmd(Game));
        Assert.Equal(new[] { Game }, _launcher.Fronted);
        Assert.Empty(_launcher.Started);
    }

    [Fact]
    public void NotRunning_IsStarted()
    {
        var cmd = Cmd(Game);
        _opener.Open(cmd);
        Assert.Same(cmd, Assert.Single(_launcher.Started));
        Assert.Empty(_launcher.Fronted);
    }

    [Fact]
    public void SecondPressWithinTheDebounce_IsIgnored_AfterItStartsAgain()
    {
        _opener.Open(Cmd(Game));
        _time.Advance(TimeSpan.FromSeconds(4.9));
        _opener.Open(Cmd(Game));
        Assert.Single(_launcher.Started);

        _time.Advance(TimeSpan.FromSeconds(0.2));
        _opener.Open(Cmd(Game));
        Assert.Equal(2, _launcher.Started.Count);
    }

    [Fact]
    public void FailedStart_DoesNotBlockTheNextPress()
    {
        _launcher.StartSucceeds = false;
        _opener.Open(Cmd(Game));
        _opener.Open(Cmd(Game));
        Assert.Equal(2, _launcher.Started.Count);
    }

    [Fact]
    public void Url_IsUntracked_AndOpensOnEveryPress()
    {
        var url = Cmd("steam://rungameid/570");
        Assert.Null(_opener.TrackedPath(url));
        Assert.Equal(ProgramState.Untracked, _opener.State(url));
        _opener.Open(url);
        _opener.Open(url);
        Assert.Equal(2, _launcher.Started.Count);
    }

    [Fact]
    public void SchemeUrl_IsUntracked()
    {
        Assert.Equal(ProgramState.Untracked, _opener.State(Cmd("ms-settings:sound")));
        Assert.Equal(ProgramState.Untracked, _opener.State(Cmd(@"shell:AppsFolder\Doom.exe")));
    }

    [Fact]
    public void QuotedSavedPath_IsTracked_AndStartsUnquoted()
    {
        var cmd = Cmd("\"" + Game + "\"");
        Assert.True(ProgramOpener.IsTrackable(cmd));
        Assert.Equal(Game, _opener.TrackedPath(cmd));

        _opener.Open(cmd);
        Assert.Equal(Game, Assert.Single(_launcher.Started).Path);

        _launcher.SetRunning(Game, true);
        _opener.Open(cmd);
        Assert.Equal(new[] { Game }, _launcher.Fronted);
    }

    [Fact]
    public void Document_IsUntracked()
        => Assert.Equal(ProgramState.Untracked, _opener.State(Cmd(@"C:\notes\todo.txt")));

    [Fact]
    public void Shortcut_IsTrackedByItsExeTarget()
    {
        _shortcuts[@"C:\Desktop\Doom.lnk"] = Game;
        var cmd = Cmd(@"C:\Desktop\Doom.lnk");
        Assert.Equal(Game, _opener.TrackedPath(cmd));
        Assert.Equal(ProgramState.NotRunning, _opener.State(cmd));

        _launcher.SetRunning(Game, true);
        Assert.Equal(ProgramState.Running, _opener.State(cmd));
        _opener.Open(cmd);
        Assert.Equal(new[] { Game }, _launcher.Fronted);
    }

    [Fact]
    public void ShortcutToANonExe_IsUntracked()
    {
        _shortcuts[@"C:\Desktop\Notes.lnk"] = @"C:\notes\todo.txt";
        Assert.Equal(ProgramState.Untracked, _opener.State(Cmd(@"C:\Desktop\Notes.lnk")));
    }

    [Fact]
    public void EmptyPath_DoesNothing()
    {
        _opener.Open(Cmd("  "));
        Assert.Empty(_launcher.Started);
        Assert.Equal(ProgramState.Untracked, _opener.State(Cmd("")));
        Assert.Equal(ProgramState.Untracked, _opener.State(null));
    }

    [Fact]
    public void Watch_PassesTheDistinctTrackedExecutables()
    {
        _shortcuts[@"C:\Desktop\Doom.lnk"] = Game;
        _opener.Watch(new[] { Cmd(Game), Cmd(@"C:\Desktop\Doom.lnk"), Cmd("steam://x"), Cmd(@"C:\a\B.EXE") });
        Assert.Equal(new[] { Game, @"C:\a\B.EXE" }, _launcher.Watched);
    }

    [Fact]
    public void LauncherChanges_AreForwarded()
    {
        var raised = 0;
        _opener.Changed += (_, _) => raised++;
        _launcher.SetRunning(Game, true);
        Assert.Equal(1, raised);
    }
}
