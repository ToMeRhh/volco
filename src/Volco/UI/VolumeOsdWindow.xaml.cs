using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Volco.Input;
using Windows.Graphics;

namespace Volco.UI;

/// <summary>
/// Borderless, click-through, always-on-top flyout shown bottom-center when the
/// user changes volume or toggles mute via the controller. Reused as a single
/// instance; <see cref="Show"/> updates content, repositions, and resets an
/// auto-hide timer.
/// </summary>
public sealed partial class VolumeOsdWindow : Window
{
    // Logical (DIP) sizes; converted to physical pixels per the display scale.
    private const double LogicalWidth = 360;
    private const double LogicalHeight = 56;
    private const double LogicalBottomMargin = 80;
    private static readonly TimeSpan VisibleDuration = TimeSpan.FromMilliseconds(1500);

    // Volume / muted glyphs from Segoe Fluent Icons.
    private const string GlyphVolume = "\uE767";
    private const string GlyphMuted = "\uE74F";

    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _hideTimer;
    private readonly AppWindow _appWindow;
    private readonly nint _hwnd;
    private Storyboard? _fadeOut;
    private bool _isVisible;

    public VolumeOsdWindow()
    {
        InitializeComponent();

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        ConfigureWindow();

        _hideTimer = _dispatcher.CreateTimer();
        _hideTimer.Interval = VisibleDuration;
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => BeginHide();

        // Realize the XAML content once so later AppWindow.Show calls render
        // correctly. WS_EX_NOACTIVATE (set above) keeps this from stealing focus.
        Activate();
        _appWindow.Hide();
    }

    /// <summary>Updates the flyout content and shows it. Safe to call from any thread.</summary>
    public void Show(VolumeChangeNotification n)
    {
        if (_dispatcher.HasThreadAccess)
            ShowCore(n);
        else
            _dispatcher.TryEnqueue(() => ShowCore(n));
    }

    private void ShowCore(VolumeChangeNotification n)
    {
        LabelText.Text = n.Label;

        if (n.Kind == OsdKind.Mute || n.Muted)
        {
            Icon.Glyph = n.Muted ? GlyphMuted : GlyphVolume;
            if (n.Kind == OsdKind.Mute)
            {
                ValueText.Text = n.Muted ? "Muted" : "On";
                LevelBar.Opacity = n.Muted ? 0.4 : 1.0;
            }
            else
            {
                ValueText.Text = $"{n.Level * 100:0}%";
                LevelBar.Opacity = 0.4;
            }
        }
        else
        {
            Icon.Glyph = GlyphVolume;
            ValueText.Text = $"{n.Level * 100:0}%";
            LevelBar.Opacity = 1.0;
        }

        LevelBar.Value = Math.Clamp(n.Level * 100, 0, 100);

        // Keep opacity solid; stopping any in-flight fade is cheap and flicker-free.
        _fadeOut?.Stop();
        Root.Opacity = 1.0;

        // Only show + reposition on the hidden->visible transition. Re-showing or
        // repositioning on every slider tick causes the window to flash.
        if (!_isVisible)
        {
            Reposition();
            _appWindow.Show(activateWindow: false);
            _isVisible = true;
        }

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void Reposition()
    {
        double scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        int w = (int)Math.Round(LogicalWidth * scale);
        int h = (int)Math.Round(LogicalHeight * scale);
        int margin = (int)Math.Round(LogicalBottomMargin * scale);

        var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest);
        var work = area.WorkArea;
        int x = work.X + (work.Width - w) / 2;
        int y = work.Y + work.Height - h - margin;
        _appWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    private void BeginHide()
    {
        _fadeOut = new Storyboard();
        var fade = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(250)),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fade, Root);
        Storyboard.SetTargetProperty(fade, "Opacity");
        _fadeOut.Children.Add(fade);
        _fadeOut.Completed += (_, _) =>
        {
            _appWindow.Hide();
            _isVisible = false;
        };
        _fadeOut.Begin();
    }

    private void ConfigureWindow()
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        _appWindow.IsShownInSwitchers = false;
        // Actual size/position is set per-show in Reposition (DPI-aware).

        // No-activate + click-through so the flyout never steals focus or blocks input.
        const int GWL_EXSTYLE = -20;
        const long WS_EX_NOACTIVATE = 0x08000000;
        const long WS_EX_TRANSPARENT = 0x00000020;
        const long WS_EX_TOOLWINDOW = 0x00000080;
        var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new nint(ex));

        // Rounded corners on Windows 11; harmless (ignored) on Windows 10.
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const int DWMWCP_ROUND = 2;
        int pref = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
}
