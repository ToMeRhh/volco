using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Volco.App.Controls;
using Volco.App.Platform;
using Volco.Core.Input;

namespace Volco.App.Osd;

/// <summary>Durations of the OSD lifecycle; injectable so tests do not sleep.</summary>
internal sealed record OsdTimings(TimeSpan Visible, TimeSpan Fade, TimeSpan Coalesce, TimeSpan FadeStep)
{
    public static OsdTimings Default { get; } = new(
        TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(16), TimeSpan.FromMilliseconds(16));
}

/// <summary>
/// Borderless, click-through, never-activating pill shown bottom-centre when a slider moves or a
/// mute button is pressed or the output device is switched. Created once, kept hidden, re-shown. Portable Avalonia code; the only
/// platform part is <see cref="IWindowEffects.MakeClickThrough"/>, applied once from the first Opened.
/// </summary>
public sealed partial class VolumeOsdWindow : Window
{
    private const double BottomMarginDip = 80;

    private readonly IWindowEffects _effects;
    private readonly OsdTimings _timings;
    private readonly VolumeOsdViewModel _vm = new();
    private readonly Border _root;
    private readonly TextBlock _label;
    private readonly TextBlock _value;
    private readonly ProgressBar _bar;
    private readonly PathIcon _volumeIcon;
    private readonly PathIcon _mutedIcon;
    private readonly PathIcon _speakerIcon;
    private readonly PathIcon _presetIcon;
    private readonly Image _deviceIcon;
    private readonly Panel _deviceMuteSlash;
    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _fadeTimer;
    private readonly Stopwatch _fadeClock = new();

    private readonly object _gate = new();
    private VolumeChangeNotification? _pending;
    private bool _flushPosted;
    private long _lastFlushTimestamp;
    private bool _hasFlushed;

    private bool _clickThroughApplied;
    private bool _shown;   // hidden -> visible transition tracking (includes the fade)
    private bool _closed;

    public VolumeOsdWindow(IWindowEffects effects) : this(effects, OsdTimings.Default) { }

    internal VolumeOsdWindow(IWindowEffects effects, OsdTimings timings)
    {
        _effects = effects;
        _timings = timings;
        AvaloniaXamlLoader.Load(this);
        _root = this.FindControl<Border>("Root")!;
        _label = this.FindControl<TextBlock>("LabelText")!;
        _value = this.FindControl<TextBlock>("ValueText")!;
        _bar = this.FindControl<ProgressBar>("LevelBar")!;
        _volumeIcon = this.FindControl<PathIcon>("VolumeIcon")!;
        _mutedIcon = this.FindControl<PathIcon>("MutedIcon")!;
        _speakerIcon = this.FindControl<PathIcon>("SpeakerIcon")!;
        _presetIcon = this.FindControl<PathIcon>("PresetIcon")!;
        _deviceIcon = this.FindControl<Image>("DeviceIcon")!;
        _deviceMuteSlash = this.FindControl<Panel>("DeviceMuteSlash")!;

        _hideTimer = new DispatcherTimer { Interval = _timings.Visible };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); BeginFade(); };
        _fadeTimer = new DispatcherTimer { Interval = _timings.FadeStep };
        _fadeTimer.Tick += (_, _) => FadeStep();

        Opened += OnOpened;
        Closed += (_, _) =>
        {
            _closed = true;
            _hideTimer.Stop();
            _fadeTimer.Stop();
        };
    }

    /// <summary>Number of UI-thread repaints performed (coalescing/change-only is observable).</summary>
    internal int RenderCount { get; private set; }

    /// <summary>Opacity of the pill (the fade target).</summary>
    internal double PillOpacity => _root.Opacity;

    /// <summary>True from the hidden-to-visible transition until the fade ends and the window hides.</summary>
    internal bool IsOsdShown => _shown;

    internal TextBlock LabelBlock => _label;
    internal TextBlock ValueBlock => _value;
    internal ProgressBar LevelBarControl => _bar;
    internal PathIcon VolumeGlyph => _volumeIcon;
    internal PathIcon MutedGlyph => _mutedIcon;
    internal PathIcon SpeakerGlyph => _speakerIcon;
    internal PathIcon PresetGlyph => _presetIcon;
    internal Image DeviceGlyph => _deviceIcon;
    internal Panel MuteSlash => _deviceMuteSlash;

    /// <summary>Shows the notification. Safe to call from any thread; posts to the UI thread itself.</summary>
    public void Show(VolumeChangeNotification n)
    {
        lock (_gate)
        {
            _pending = n;           // latest wins
            if (_flushPosted) return;
            _flushPosted = true;
        }
        Dispatcher.UIThread.Post(Flush);   // never Invoke: events can arrive under transport locks
    }

    private void Flush()
    {
        // 60 Hz cap: if we repainted less than one coalescing window ago, wait out the remainder.
        if (_hasFlushed)
        {
            var elapsed = Stopwatch.GetElapsedTime(_lastFlushTimestamp);
            if (elapsed < _timings.Coalesce)
            {
                DispatcherTimer.RunOnce(FlushNow, _timings.Coalesce - elapsed);
                return;
            }
        }
        FlushNow();
    }

    private void FlushNow()
    {
        VolumeChangeNotification? n;
        lock (_gate)
        {
            n = _pending;
            _pending = null;
            _flushPosted = false;
        }
        if (n is null || _closed) return;

        _hasFlushed = true;
        _lastFlushTimestamp = Stopwatch.GetTimestamp();

        if (_vm.Apply(n)) Render();

        // A new Show during the fade cancels it and restores opacity.
        _fadeTimer.Stop();
        _root.Opacity = 1.0;

        // Reposition and show only on the hidden -> visible transition; doing it per slider tick flashes.
        if (!_shown)
        {
            Reposition();
            _shown = true;
            base.Show();
        }

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void Render()
    {
        RenderCount++;
        _label.Text = _vm.Label;
        _value.Text = _vm.ValueText;
        _bar.Value = _vm.Level;
        _bar.Opacity = _vm.LevelBarOpacity;
        _mutedIcon.IsVisible = _vm.Glyph == OsdGlyph.Muted;
        _speakerIcon.IsVisible = _vm.Glyph == OsdGlyph.Speaker;
        _volumeIcon.IsVisible = _vm.Glyph == OsdGlyph.Volume;
        _presetIcon.IsVisible = _vm.Glyph == OsdGlyph.Preset;
        _deviceIcon.Source = _vm.DeviceIcon is { } icon ? ScreenIcons.Bitmap(icon) : null;
        _deviceIcon.IsVisible = _vm.Glyph == OsdGlyph.Device;
        _deviceIcon.Opacity = _vm.DeviceIconMuted ? 0.4 : 1.0;
        _deviceMuteSlash.IsVisible = _vm.DeviceIconMuted;
        _bar.IsVisible = _vm.ShowsLevel;
    }

    private void Reposition()
    {
        var screen = Screens?.Primary;
        if (screen is null) return;   // headless or no screen: keep the default position
        var scale = screen.Scaling;
        var w = (int)Math.Round(Width * scale);
        var h = (int)Math.Round(Height * scale);
        var margin = (int)Math.Round(BottomMarginDip * scale);
        var wa = screen.WorkingArea;
        Position = new PixelPoint(wa.X + (wa.Width - w) / 2, wa.Y + wa.Height - h - margin);
    }

    private void BeginFade()
    {
        _fadeClock.Restart();
        _fadeTimer.Start();
    }

    private void FadeStep()
    {
        var t = _timings.Fade <= TimeSpan.Zero ? 1.0 : _fadeClock.Elapsed / _timings.Fade;
        if (t < 1.0)
        {
            _root.Opacity = 1.0 - t;
            return;
        }
        _fadeTimer.Stop();
        _root.Opacity = 0;
        _shown = false;
        Hide();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        // Once, and only after the platform handle exists (Opened is raised after the native window is created).
        if (_clickThroughApplied) return;
        _clickThroughApplied = true;
        _effects.MakeClickThrough(this);
    }
}
