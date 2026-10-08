using Volco.App.ViewModels;
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Runtime;
using Volco.Core.Tests.Audio;
using Volco.Core.Tests.Runtime;

namespace Volco.App.Tests;

/// <summary>Queues posted actions until <see cref="RunAll"/>, so tests can see what ran when.</summary>
internal sealed class RecordingDispatcher : IUiDispatcher
{
    private readonly Queue<Action> _queue = new();

    public int Pending { get { lock (_queue) return _queue.Count; } }

    public void Post(Action action)
    {
        lock (_queue) _queue.Enqueue(action);
    }

    public void RunAll()
    {
        while (true)
        {
            Action next;
            lock (_queue)
            {
                if (_queue.Count == 0) return;
                next = _queue.Dequeue();
            }
            next();
        }
    }
}

/// <summary>A started headless runtime over fakes, for ViewModel and page tests.</summary>
internal sealed class TestRig : IDisposable
{
    public ConfigManager Config { get; }
    public FakeAudioEngine Engine { get; }
    public FakeControllerConnection? Connection { get; private set; }
    public VolcoRuntime Runtime { get; }

    /// <summary>The default options without the connect grace, so a test sees the link state the
    /// fake transport implies at once.</summary>
    public static readonly VolcoRuntimeOptions NoGrace = new() { ConnectGrace = TimeSpan.Zero };

    /// <param name="custom">Media, program and script services; null: none (those actions are
    /// unavailable, as on a platform without them).</param>
    public TestRig(FakeAudioEngine? engine = null, VolcoRuntimeOptions? options = null, CustomActionServices? custom = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "volco-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Config = new ConfigManager(Path.Combine(dir, "config.json"));
        Config.Load();
        Engine = engine ?? new FakeAudioEngine();
        Runtime = new VolcoRuntime(Config, Engine, audioReady: true, connectionFactory: cfg =>
            Connection = new FakeControllerConnection(cfg), options: options ?? NoGrace, custom: custom);
        Runtime.Start();
    }

    public SettingsViewModel CreateSettings(IUiDispatcher ui)
        => new(Config, Engine, Runtime, ui);

    public void Dispose() => Runtime.Dispose();
}
