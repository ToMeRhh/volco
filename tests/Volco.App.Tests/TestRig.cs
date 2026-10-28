using Volco.Core.Communication;
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
    /// <param name="configJson">config.json as it is on disk before the app starts; null: none.</param>
    /// <param name="serialPorts">The seam behind the port list; null: names only.</param>
    /// <param name="realTransports">True: the runtime builds its own transports (Automatic USB, with
    /// <c>options.SerialFactory</c>), so <see cref="Connection"/> stays null.</param>
    public TestRig(FakeAudioEngine? engine = null, VolcoRuntimeOptions? options = null, CustomActionServices? custom = null,
                   string? configJson = null, ISerialPortInfo? serialPorts = null, bool realTransports = false)
    {
        var dir = Path.Combine(Path.GetTempPath(), "volco-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        if (configJson is not null) File.WriteAllText(Path.Combine(dir, "config.json"), configJson);
        Config = new ConfigManager(Path.Combine(dir, "config.json"));
        Config.Load();
        Engine = engine ?? new FakeAudioEngine();
        Runtime = new VolcoRuntime(Config, Engine, audioReady: true, connectionFactory: realTransports ? null : cfg =>
            Connection = new FakeControllerConnection(cfg), options: options ?? NoGrace, custom: custom, serialPorts: serialPorts);
        Runtime.Start();
    }

    /// <summary>The run's Wi-Fi switch (the Connection dialog's Wi-Fi card), made with the first
    /// settings view model; its config edits are posted to that view model's dispatcher.</summary>
    public WifiSwitch? Wifi { get; private set; }

    public SettingsViewModel CreateSettings(IUiDispatcher ui, Volco.App.Platform.IAppIcons? appIcons = null)
    {
        Wifi ??= new WifiSwitch(Runtime, a =>
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ui.Post(() =>
            {
                try { a(); done.SetResult(); }
                catch (Exception e) { done.SetException(e); }
            });
            return done.Task;
        });
        return new(Config, Engine, Runtime, ui, appIcons: appIcons, wifi: Wifi);
    }

    public void Dispose()
    {
        Wifi?.Dispose();
        Runtime.Dispose();
    }
}
