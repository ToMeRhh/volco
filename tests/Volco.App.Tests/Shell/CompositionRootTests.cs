using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Volco.App.Platform;
using Volco.Core.Tests.Audio;
using Volco.Core.Tests.Runtime;
using Xunit;

namespace Volco.App.Tests.Shell;

public class CompositionRootTests
{
    private sealed class RecordingEffects : IWindowEffects
    {
        public List<Window> Calls { get; } = new();
        public void MakeClickThrough(Window window) => Calls.Add(window);
    }

    private static CompositionRoot Create(FakeAudioEngine engine, bool audioReady)
    {
        var dir = Path.Combine(Path.GetTempPath(), "volco-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new CompositionRoot(
            Path.Combine(dir, "config.json"),
            () => new PlatformServices(engine, audioReady, new RecordingEffects()),
            connectionFactory: cfg => new FakeControllerConnection(cfg),
            createTray: false);
    }

    [AvaloniaFact]
    public void CompositionRoot_AudioNotReady_ShowsSettingsOnStart()
    {
        var root = Create(new FakeAudioEngine(), audioReady: false);
        try
        {
            root.Start();
            Assert.NotNull(root.SettingsWindow);
            Assert.True(root.SettingsWindow!.IsVisible);
        }
        finally { root.Shutdown(); }
    }

    [AvaloniaFact]
    public void CompositionRoot_CheckForUpdates_ReusesAndReactivatesOneWindow()
    {
        var root = Create(new FakeAudioEngine(), audioReady: true);
        try
        {
            root.Start();
            Assert.Null(root.UpdateWindow);

            root.CheckForUpdates();
            var first = root.UpdateWindow;
            Assert.NotNull(first);
            Assert.True(first!.IsVisible);

            root.CheckForUpdates();
            Assert.Same(first, root.UpdateWindow);

            first.Close();
            Assert.Null(root.UpdateWindow);
            root.CheckForUpdates();
            Assert.NotSame(first, root.UpdateWindow);
        }
        finally { root.Shutdown(); }
    }

    [AvaloniaFact]
    public void CompositionRoot_AudioReady_DoesNotShowSettingsOnStart()
    {
        var root = Create(new FakeAudioEngine(), audioReady: true);
        try
        {
            root.Start();
            Assert.Null(root.SettingsWindow);
        }
        finally { root.Shutdown(); }
    }

    [AvaloniaFact]
    public void CompositionRoot_Shutdown_DisposesRuntimeBeforeEngine()
    {
        var engine = new FakeAudioEngine();
        var subscribersAtDispose = -1;
        engine.OnDispose = () => subscribersAtDispose = engine.DeviceVolumeChangedSubscriberCount;
        var root = Create(engine, audioReady: true);

        root.Start();
        // Proves the probe works: the started runtime is subscribed to the engine.
        Assert.True(engine.DeviceVolumeChangedSubscriberCount > 0);

        root.Shutdown();

        Assert.True(engine.Disposed);
        Assert.Equal(0, subscribersAtDispose);
    }
}
