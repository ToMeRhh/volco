using Microsoft.Extensions.Time.Testing;
using Volco.Core.Config;
using Volco.Core.Scripts;
using Volco.Core.Tests.Fakes;
using Xunit;

namespace Volco.Core.Tests.Scripts;

public class ScriptRunnerTests
{
    private const string Script = @"C:\s\backup.bat";
    private static readonly IReadOnlyDictionary<string, string> Env = new Dictionary<string, string> { ["VOLCO_BUTTON"] = "1" };
    private readonly FakeProcessStarter _starter = new();
    private readonly FakeTimeProvider _time = new();
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase) { Script };
    private readonly ScriptRunner _runner;
    private int _changes;

    public ScriptRunnerTests()
    {
        _runner = new ScriptRunner(_starter, _time, name => @"C:\Windows\System32\" + name, _files.Contains);
        _runner.Changed += (_, _) => Interlocked.Increment(ref _changes);
    }

    private static ButtonActionConfig Action(WhileRunningPolicy policy = WhileRunningPolicy.Ignore, string path = Script)
        => new() { Action = ButtonActionType.RunScript, Command = new CommandConfig { Path = path }, WhileRunning = policy };

    [Fact]
    public void Run_StartsTheInterpreter_WithTheEnvironment()
    {
        var action = Action();
        _runner.Run(action, Env);

        var (spec, _) = Assert.Single(_starter.Runs);
        Assert.Equal(@"C:\Windows\System32\cmd.exe", spec.FileName);
        Assert.Equal("1", spec.Environment["VOLCO_BUTTON"]);
        Assert.Equal(new ScriptStatus(true, null), _runner.Status(action));
        Assert.True(_changes > 0);
    }

    [Fact]
    public void Ignore_DropsPressesWhileItRuns()
    {
        var action = Action();
        _runner.Run(action, Env);
        _runner.Run(action, Env);
        Assert.Single(_starter.Runs);

        _starter.Runs[0].Run.End(0);
        Assert.Equal(ScriptStatus.Idle, _runner.Status(action));
        _runner.Run(action, Env);
        Assert.Equal(2, _starter.Runs.Count);
    }

    [Fact]
    public void StartAnother_RunsAgain_AndRunsUntilAllEnd()
    {
        var action = Action(WhileRunningPolicy.StartAnother);
        _runner.Run(action, Env);
        _runner.Run(action, Env);
        Assert.Equal(2, _starter.Runs.Count);

        _starter.Runs[0].Run.End(0);
        Assert.True(_runner.Status(action).Running);
        _starter.Runs[1].Run.End(0);
        Assert.False(_runner.Status(action).Running);
    }

    [Fact]
    public void ExitZero_IsSilent()
    {
        var action = Action();
        _runner.Run(action, Env);
        _starter.Runs[0].Run.End(0);
        Assert.Equal(ScriptStatus.Idle, _runner.Status(action));
    }

    [Fact]
    public void UnknownExitCode_IsNotAFailure()
    {
        var action = Action();
        _runner.Run(action, Env);
        _starter.Runs[0].Run.End(null);
        Assert.Equal(ScriptStatus.Idle, _runner.Status(action));
    }

    [Fact]
    public void NonZeroExit_FlashesForSixSteps()
    {
        var action = Action();
        _runner.Run(action, Env);
        _starter.Runs[0].Run.Output.Add("boom");
        _starter.Runs[0].Run.End(2);

        Assert.Equal(new ScriptStatus(false, 0), _runner.Status(action));
        for (int step = 1; step < ScriptRunner.FlashSteps; step++)
        {
            _time.Advance(ScriptRunner.FlashStepInterval);
            Assert.Equal(step, _runner.Status(action).FlashStep);
        }
        _time.Advance(ScriptRunner.FlashStepInterval);
        Assert.Equal(ScriptStatus.Idle, _runner.Status(action));
    }

    [Fact]
    public void MissingFile_Flashes_WithoutStarting()
    {
        var action = Action(path: @"C:\s\gone.bat");
        _runner.Run(action, Env);
        Assert.Empty(_starter.Runs);
        Assert.Equal(new ScriptStatus(false, 0), _runner.Status(action));
    }

    [Fact]
    public void NoScriptChosen_Flashes()
    {
        var action = new ButtonActionConfig { Action = ButtonActionType.RunScript };
        _runner.Run(action, Env);
        Assert.Empty(_starter.Runs);
        Assert.Equal(0, _runner.Status(action).FlashStep);
    }

    [Fact]
    public void StartFailure_Flashes_AndAllowsTheNextPress()
    {
        var action = Action();
        _starter.StartFails = new System.ComponentModel.Win32Exception("The system cannot find the file specified");
        _runner.Run(action, Env);
        Assert.Equal(new ScriptStatus(false, 0), _runner.Status(action));

        _starter.StartFails = null;
        _runner.Run(action, Env);
        Assert.Single(_starter.Runs);
    }

    [Fact]
    public void RunningAndFlashing_AreReportedTogether()
    {
        var action = Action(WhileRunningPolicy.StartAnother);
        _runner.Run(action, Env);
        _runner.Run(action, Env);
        _starter.Runs[0].Run.End(1);
        Assert.Equal(new ScriptStatus(true, 0), _runner.Status(action));
    }

    [Fact]
    public void StateIsPerAction()
    {
        var a = Action();
        var b = Action();
        _runner.Run(a, Env);
        Assert.True(_runner.Status(a).Running);
        Assert.Equal(ScriptStatus.Idle, _runner.Status(b));
        _runner.Run(b, Env);
        Assert.Equal(2, _starter.Runs.Count);
    }

    [Fact]
    public void ARunKeepsGoing_WhenItsCommandIsEdited()
    {
        var action = Action();
        _runner.Run(action, Env);
        action.Command!.Path = @"C:\s\other.bat";
        Assert.True(_runner.Status(action).Running);
        _starter.Runs[0].Run.End(0);
        Assert.False(_runner.Status(action).Running);
    }

    [Fact]
    public void AfterDispose_NothingRuns()
    {
        var action = Action();
        _runner.Dispose();
        _runner.Run(action, Env);
        Assert.Empty(_starter.Runs);
    }
}

/// <summary>The real starter, with /bin/sh standing in for an interpreter (Linux CI).</summary>
public class SystemProcessStarterTests
{
    [Fact]
    public async Task CapturesTheExitCode_TheOutputTail_AndTheEnvironment()
    {
        if (!File.Exists("/bin/sh")) return;
        var env = new Dictionary<string, string> { ["VOLCO_GESTURE"] = "double" };
        var spec = new ProcessSpec("/bin/sh",
            "-c \"i=0; while [ $i -lt 30 ]; do echo line$i; i=$((i+1)); done; echo $VOLCO_GESTURE; exit 3\"",
            "/", false, env);

        var run = new SystemProcessStarter().Start(spec);
        var code = await run.Exited.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, code);
        Assert.Equal(ScriptRunner.TailLines, run.OutputTail.Count);
        Assert.Contains("double", run.OutputTail);
        Assert.Contains("line29", run.OutputTail);
        Assert.DoesNotContain("line0", run.OutputTail);
    }

    [Fact]
    public async Task AFastExit_IsSeen()
    {
        if (!File.Exists("/bin/true")) return;
        var run = new SystemProcessStarter().Start(new ProcessSpec("/bin/true", "", "/", false, new Dictionary<string, string>()));
        Assert.Equal(0, await run.Exited.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void AMissingProgram_Throws()
        => Assert.ThrowsAny<Exception>(() => new SystemProcessStarter().Start(
            new ProcessSpec("/nonexistent/volco-test", "", "/", false, new Dictionary<string, string>())));
}
