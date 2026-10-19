using Volco.Core.Config;
using Volco.Core.Programs;
using Volco.Core.Scripts;
using Xunit;

namespace Volco.Core.Tests.Scripts;

public class ScriptInterpretersTests
{
    private static readonly IReadOnlyDictionary<string, string> Env = new Dictionary<string, string> { ["VOLCO_BUTTON"] = "2" };

    private static Func<string, string?> OnPath(params string[] installed)
        => name => installed.Contains(name, StringComparer.OrdinalIgnoreCase) ? @"C:\bin\" + name : null;

    private static ProcessSpec Resolve(string path, string args = "", string wd = "", params string[] installed)
        => ScriptInterpreters.Resolve(new CommandConfig { Path = path, Arguments = args, WorkingDirectory = wd },
                                      OnPath(installed.Length == 0 ? new[] { "cmd.exe" } : installed), Env);

    [Fact]
    public void Batch_RunsThroughCmd_WithAQuotedPath()
    {
        var spec = Resolve(@"C:\My Scripts\backup now.bat", "--fast \"a b\"");
        Assert.Equal(@"C:\bin\cmd.exe", spec.FileName);
        Assert.Equal("/d /s /c \"\"C:\\My Scripts\\backup now.bat\" --fast \"a b\"\"", spec.Arguments);
        Assert.False(spec.UseShell);
        Assert.Equal(@"C:\My Scripts", spec.WorkingDirectory);
        Assert.Same(Env, spec.Environment);
    }

    [Fact]
    public void Cmd_WithoutArguments()
        => Assert.Equal("/d /s /c \"\"C:\\x.cmd\"\"", Resolve(@"C:\x.cmd").Arguments);

    [Fact]
    public void PowerShell_PrefersPwsh_NoProfile()
    {
        var spec = Resolve(@"C:\s\hook.ps1", "-Url x", installed: new[] { "pwsh.exe", "powershell.exe" });
        Assert.Equal(@"C:\bin\pwsh.exe", spec.FileName);
        Assert.Equal("-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"C:\\s\\hook.ps1\" -Url x", spec.Arguments);
    }

    [Fact]
    public void PowerShell_FallsBackToWindowsPowerShell()
    {
        Assert.Equal(@"C:\bin\powershell.exe", Resolve(@"C:\s\hook.ps1", installed: new[] { "powershell.exe" }).FileName);
        Assert.Equal("powershell.exe", Resolve(@"C:\s\hook.ps1", installed: new[] { "nothing.exe" }).FileName);
    }

    [Fact]
    public void Python_PrefersThePyLauncher()
    {
        Assert.Equal(@"C:\bin\py.exe", Resolve(@"C:\s\a.py", installed: new[] { "py.exe", "python.exe" }).FileName);
        var spec = Resolve(@"C:\s\a.py", "1 2", installed: new[] { "python.exe" });
        Assert.Equal(@"C:\bin\python.exe", spec.FileName);
        Assert.Equal("\"C:\\s\\a.py\" 1 2", spec.Arguments);
    }

    [Fact]
    public void Node_RunsJs()
        => Assert.Equal(@"C:\bin\node.exe", Resolve(@"C:\s\a.js", installed: new[] { "node.exe" }).FileName);

    [Fact]
    public void Exe_RunsItself()
    {
        var spec = Resolve(@"C:\tools\notify.exe", "on");
        Assert.Equal(@"C:\tools\notify.exe", spec.FileName);
        Assert.Equal("on", spec.Arguments);
        Assert.False(spec.UseShell);
    }

    [Theory]
    [InlineData(@"C:\s\macro.ahk")]
    [InlineData(@"C:\s\old.vbs")]
    [InlineData(@"C:\s\NOEXT")]
    public void OtherFiles_UseTheirFileAssociation(string path)
    {
        var spec = Resolve(path, "x");
        Assert.True(spec.UseShell);
        Assert.Equal(path, spec.FileName);
        Assert.Equal("x", spec.Arguments);
    }

    [Fact]
    public void ExtensionsAreCaseInsensitive()
        => Assert.Equal(@"C:\bin\cmd.exe", Resolve(@"C:\S\RUN.BAT").FileName);

    [Fact]
    public void WorkingDirectory_ConfiguredWins_RootStaysRooted()
    {
        Assert.Equal(@"D:\work", Resolve(@"C:\s\a.bat", wd: @"D:\work").WorkingDirectory);
        Assert.Equal(@"C:\", Resolve(@"C:\a.bat").WorkingDirectory);
    }

    [Fact]
    public void DisplayNames()
    {
        Assert.Equal("backup now", CommandPath.DisplayName(new CommandConfig { Path = @"C:\My Scripts\backup now.bat" }));
        Assert.Equal("Backup", CommandPath.DisplayName(new CommandConfig { Path = @"C:\x.bat", Name = " Backup " }));
        Assert.Equal("rungameid/570", CommandPath.DisplayName(new CommandConfig { Path = "steam://rungameid/570" }));
        Assert.Equal("sound", CommandPath.DisplayName(new CommandConfig { Path = "ms-settings:sound" }));
        Assert.Equal("lights", CommandPath.DisplayName(new CommandConfig { Path = "\"C:\\Scripts\\lights.bat\"" }));
        Assert.Equal("", CommandPath.DisplayName(new CommandConfig()));
        Assert.Equal("", CommandPath.DisplayName(null));
    }
}
