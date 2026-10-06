using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Volco.App.Platform;
using Volco.Core.Runtime;
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

    private static CompositionRoot Create(FakeAudioEngine engine, bool audioReady,
                                          Action<CompositionRoot>? onConnect = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "volco-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        CompositionRoot? root = null;
        root = new CompositionRoot(
            Path.Combine(dir, "config.json"),
            () => new PlatformServices(engine, audioReady, new RecordingEffects()),
            connectionFactory: cfg =>
            {
                onConnect?.Invoke(root!);
                return new FakeControllerConnection(cfg);
            },
            createTray: false);
        return root;
    }

    [AvaloniaFact]
    public void TheTrayUpdateState_FollowsTheBundledFirmware()
    {
        var dir = Path.Combine(Path.GetTempPath(), "volco-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        FakeControllerConnection? connection = null;
        var root = new CompositionRoot(Path.Combine(dir, "config.json"),
            () => new PlatformServices(new FakeAudioEngine(), true, new RecordingEffects(), null,
                                       Volco.Core.Tests.Firmware.MemoryFirmwareSource.Valid()),
            connectionFactory: cfg => connection = new FakeControllerConnection(cfg),
            createTray: false);
        try
        {
            root.Start();
            Assert.False(root.TrayShowsFirmwareUpdate);
            connection!.SetStatus(Volco.Core.Communication.TransportStatus.Open);
            connection.RaiseMessage(new Volco.Core.Communication.HelloMessage(1, Volco.Core.Communication.ChipFamily.Esp32,
                "0000aaaa1111", Volco.Core.Communication.ControllerConfigState.None, 0, 5, new[] { Volco.Core.Communication.IndicatorKind.Led }));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(root.TrayShowsFirmwareUpdate);

            connection.RaiseMessage(new Volco.Core.Communication.HelloMessage(1, Volco.Core.Communication.ChipFamily.Esp32,
                "3f9c2a1b5d07", Volco.Core.Communication.ControllerConfigState.None, 0, 5, new[] { Volco.Core.Communication.IndicatorKind.Led }));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.False(root.TrayShowsFirmwareUpdate);
        }
        finally { root.Shutdown(); }
    }

    private static int VolumeChangedSubscribers(VolcoRuntime runtime)
        => (typeof(VolcoRuntime)
                .GetField(nameof(VolcoRuntime.VolumeChanged), BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(runtime) as Delegate)?.GetInvocationList().Length ?? 0;

    [AvaloniaFact]
    public void CompositionRoot_OsdIsSubscribedBeforeTheRuntimeConnects()
    {
        // The runtime builds its transport inside Start(); from then on a controller frame can
        // raise VolumeChanged, so the OSD must already be listening.
        var subscribersAtConnect = -1;
        var root = Create(new FakeAudioEngine(), audioReady: true,
                          onConnect: r => subscribersAtConnect = VolumeChangedSubscribers(r.Runtime!));
        try
        {
            root.Start();
            Assert.True(subscribersAtConnect > 0, $"VolumeChanged had {subscribersAtConnect} subscribers when the transport was built");
        }
        finally { root.Shutdown(); }
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
    public void CompositionRoot_CheckForUpdates_OpensSettingsOnTheGeneralPage()
    {
        var root = Create(new FakeAudioEngine(), audioReady: true);
        try
        {
            root.Start();
            Assert.Null(root.SettingsWindow);

            root.CheckForUpdates();

            Assert.NotNull(root.SettingsWindow);
            Assert.True(root.SettingsWindow!.IsVisible);
            Assert.IsType<Volco.App.Views.Pages.GeneralPage>(root.SettingsWindow.CurrentPage);
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
