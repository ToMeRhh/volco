using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Volco.App.Osd;
using Volco.App.Platform;
using Volco.Core.Communication;
using Volco.Core.Input;
using Xunit;

namespace Volco.App.Tests.Osd;

public class VolumeOsdWindowTests
{
    private sealed class RecordingEffects : IWindowEffects
    {
        public int Calls;
        public bool HandleExistedAtCall;
        public bool VisibleAtCall;
        public Window? Target;

        public void MakeClickThrough(Window window)
        {
            Calls++;
            Target = window;
            HandleExistedAtCall = window.TryGetPlatformHandle() != null;
            VisibleAtCall = window.IsVisible;
        }
    }

    private static readonly VolumeChangeNotification Vol =
        new("Spotify", 0.4f, false, OsdKind.Volume);

    private static OsdTimings Fast(int visibleMs = 60, int fadeMs = 60) => new(
        TimeSpan.FromMilliseconds(visibleMs), TimeSpan.FromMilliseconds(fadeMs),
        TimeSpan.FromMilliseconds(16), TimeSpan.FromMilliseconds(5));

    /// <summary>
    /// Pumps the dispatcher until the condition holds or time runs out. RunJobs alone never fires
    /// DispatcherTimers on the headless platform, so run short slices of the real dispatcher loop.
    /// </summary>
    private static bool PumpUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            using var slice = new CancellationTokenSource();
            slice.CancelAfter(10);
            Dispatcher.UIThread.MainLoop(slice.Token);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    [AvaloniaFact]
    public void OsdWindow_AppliesClickThroughExactlyOnceAfterOpen()
    {
        var effects = new RecordingEffects();
        var osd = new VolumeOsdWindow(effects, Fast(visibleMs: 10_000));

        Assert.Equal(0, effects.Calls);   // not before the window is opened

        osd.Show(Vol);
        osd.Show(Vol with { Level = 0.5f });
        osd.Show(Vol with { Level = 0.6f });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, effects.Calls);
        Assert.Same(osd, effects.Target);
        Assert.True(effects.HandleExistedAtCall, "platform handle must exist when click-through is applied");
        Assert.True(effects.VisibleAtCall);
        osd.Close();
    }

    [AvaloniaFact]
    public void OsdWindow_ReShowAfterHide_DoesNotReapplyClickThrough()
    {
        var effects = new RecordingEffects();
        var osd = new VolumeOsdWindow(effects, Fast());

        osd.Show(Vol);
        Assert.True(PumpUntil(() => osd.IsOsdShown));
        Assert.True(PumpUntil(() => !osd.IsOsdShown && !osd.IsVisible));

        osd.Show(Vol with { Level = 0.9f });
        Assert.True(PumpUntil(() => osd.IsVisible));

        Assert.Equal(1, effects.Calls);
        osd.Close();
    }

    [AvaloniaFact]
    public void OsdWindow_ShowFromWorkerThread_DoesNotThrow()
    {
        var osd = new VolumeOsdWindow(new RecordingEffects(), Fast(visibleMs: 10_000));

        Task.Run(() => osd.Show(Vol)).GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();

        Assert.True(osd.IsVisible);
        Assert.Equal("40%", osd.ValueBlock.Text);
        osd.Close();
    }

    [AvaloniaFact]
    public void OsdWindow_NeverActivates()
    {
        var osd = new VolumeOsdWindow(new RecordingEffects());

        Assert.False(osd.ShowActivated);
        Assert.True(osd.Topmost);
        Assert.False(osd.ShowInTaskbar);
        Assert.Equal(WindowDecorations.None, osd.WindowDecorations);
        Assert.Equal(360, osd.Width);
        Assert.Equal(56, osd.Height);
    }

    [AvaloniaFact]
    public void OsdWindow_LongLabel_UsesCharacterEllipsis()
    {
        var osd = new VolumeOsdWindow(new RecordingEffects(), Fast(visibleMs: 10_000));
        var label = new string('x', 200);

        osd.Show(Vol with { Label = label });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(label, osd.LabelBlock.Text);
        Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, osd.LabelBlock.TextTrimming);
        osd.Close();
    }

    [AvaloniaFact]
    public void OsdWindow_MuteNotification_SwapsGlyphAndText()
    {
        var osd = new VolumeOsdWindow(new RecordingEffects(), Fast(visibleMs: 10_000));

        osd.Show(new VolumeChangeNotification("Output", 0.42f, true, OsdKind.Mute));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Muted", osd.ValueBlock.Text);
        Assert.True(osd.MutedGlyph.IsVisible);
        Assert.False(osd.VolumeGlyph.IsVisible);
        Assert.False(osd.SpeakerGlyph.IsVisible);

        osd.Show(new VolumeChangeNotification("Output", 0.42f, false, OsdKind.Mute));
        PumpUntil(() => osd.ValueBlock.Text == "42%");
        Assert.Equal("42%", osd.ValueBlock.Text);
        Assert.False(osd.MutedGlyph.IsVisible);
        Assert.True(osd.VolumeGlyph.IsVisible);
        Assert.False(osd.SpeakerGlyph.IsVisible);
        osd.Close();
    }

    [AvaloniaFact]
    public void OsdWindow_DeviceNotification_ShowsSpeakerGlyphAndPercent()
    {
        var osd = new VolumeOsdWindow(new RecordingEffects(), Fast(visibleMs: 10_000));

        osd.Show(new VolumeChangeNotification("Headphones", 0.37f, false, OsdKind.Device));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Headphones", osd.LabelBlock.Text);
        Assert.Equal("37%", osd.ValueBlock.Text);
        Assert.True(osd.SpeakerGlyph.IsVisible);   // no icon known: the generic speaker
        Assert.False(osd.VolumeGlyph.IsVisible);
        Assert.False(osd.MutedGlyph.IsVisible);
        Assert.False(osd.DeviceGlyph.IsVisible);

        osd.Show(new VolumeChangeNotification("Headphones", 0.37f, true, OsdKind.Device));
        PumpUntil(() => osd.ValueBlock.Text == "Muted");
        Assert.Equal("Muted", osd.ValueBlock.Text);
        Assert.True(osd.MutedGlyph.IsVisible);
        Assert.False(osd.SpeakerGlyph.IsVisible);
        osd.Close();
    }

    [AvaloniaFact]
    public void OsdWindow_DeviceNotificationWithIcon_ShowsTheDeviceIconInsteadOfTheSpeaker()
    {
        var osd = new VolumeOsdWindow(new RecordingEffects(), Fast(visibleMs: 10_000));

        osd.Show(new VolumeChangeNotification("Headphones", 0.37f, true, OsdKind.Device, ScreenIcon.Headphones));
        Dispatcher.UIThread.RunJobs();
        Assert.True(osd.DeviceGlyph.IsVisible);
        Assert.NotNull(osd.DeviceGlyph.Data);
        Assert.False(osd.SpeakerGlyph.IsVisible);
        Assert.False(osd.MutedGlyph.IsVisible);
        Assert.False(osd.VolumeGlyph.IsVisible);
        Assert.Equal("Muted", osd.ValueBlock.Text);
        Assert.True(osd.MuteSlash.IsVisible);   // muted: struck through...
        Assert.Equal(0.4, osd.DeviceGlyph.Opacity);    // ...and dimmed

        osd.Show(new VolumeChangeNotification("Headphones", 0.37f, false, OsdKind.Device, ScreenIcon.Headphones));
        PumpUntil(() => !osd.MuteSlash.IsVisible);
        Assert.False(osd.MuteSlash.IsVisible);
        Assert.Equal(1.0, osd.DeviceGlyph.Opacity);

        osd.Show(Vol);
        PumpUntil(() => osd.VolumeGlyph.IsVisible);
        Assert.True(osd.VolumeGlyph.IsVisible);
        Assert.False(osd.DeviceGlyph.IsVisible);
        Assert.False(osd.MuteSlash.IsVisible);
        osd.Close();
    }

    [AvaloniaFact]
    public void OsdWindow_BurstOfShows_CoalescesIntoOneRepaintOfLatestValue()
    {
        var osd = new VolumeOsdWindow(new RecordingEffects(), Fast(visibleMs: 10_000));

        for (var i = 1; i <= 20; i++) osd.Show(Vol with { Level = i / 100f });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, osd.RenderCount);
        Assert.Equal("20%", osd.ValueBlock.Text);
        osd.Close();
    }

    [AvaloniaFact]
    public void OsdWindow_RepeatedIdenticalShow_DoesNotRepaint()
    {
        var osd = new VolumeOsdWindow(new RecordingEffects(), Fast(visibleMs: 10_000));

        osd.Show(Vol);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, osd.RenderCount);

        osd.Show(Vol);
        PumpUntil(() => false, timeoutMs: 100);   // let the throttled flush run
        Assert.Equal(1, osd.RenderCount);
        osd.Close();
    }

    [AvaloniaFact]
    public void OsdWindow_AutoHidesAfterIdle_ThenFadesAndHides()
    {
        var osd = new VolumeOsdWindow(new RecordingEffects(), Fast(visibleMs: 40, fadeMs: 60));

        osd.Show(Vol);
        Dispatcher.UIThread.RunJobs();
        Assert.True(osd.IsVisible);
        Assert.Equal(1.0, osd.PillOpacity);

        Assert.True(PumpUntil(() => !osd.IsVisible), "OSD should hide itself after the idle period and fade");
        Assert.False(osd.IsOsdShown);
    }

    [AvaloniaFact]
    public void OsdWindow_ShowDuringFade_CancelsFadeAndRestoresOpacity()
    {
        // Long fade so the test can reliably land inside it.
        var osd = new VolumeOsdWindow(new RecordingEffects(), Fast(visibleMs: 20, fadeMs: 5000));

        osd.Show(Vol);
        Dispatcher.UIThread.RunJobs();
        Assert.True(PumpUntil(() => osd.PillOpacity < 1.0), "fade should have started");

        osd.Show(Vol with { Level = 0.8f });
        Assert.True(PumpUntil(() => osd.ValueBlock.Text == "80%"));

        Assert.Equal(1.0, osd.PillOpacity);
        Assert.True(osd.IsVisible);
        osd.Close();
    }

    [AvaloniaFact]
    public void OsdWindow_ShowWhileVisible_DoesNotMoveWindow()
    {
        var osd = new VolumeOsdWindow(new RecordingEffects(), Fast(visibleMs: 10_000));

        osd.Show(Vol);
        Dispatcher.UIThread.RunJobs();
        var first = osd.Position;
        osd.Position = new PixelPoint(first.X + 123, first.Y + 45);   // simulate "positioned on show"

        osd.Show(Vol with { Level = 0.9f });
        PumpUntil(() => osd.ValueBlock.Text == "90%");

        Assert.Equal(new PixelPoint(first.X + 123, first.Y + 45), osd.Position);   // not recomputed
        osd.Close();
    }
}
