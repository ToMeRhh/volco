using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;

namespace Volco.App.ViewModels.Hardware;

/// <summary>One entry of a duration list: its words and its seconds (null for Never).</summary>
public sealed record DurationChoice(string Label, uint? Seconds, bool IsCustom = false)
{
    /// <summary>The label: what a combo box shows.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// The Hardware inspector of the controller's settings (hardware-page spec §6.6): Screens, Sliders,
/// Buttons and Wi-Fi, each opened and closed on its own, in plain words over the draft's raw fields (which stay under
/// Fine tune). A value off a list shows as "Custom (90 s)" and is kept. Every edit goes through
/// <see cref="ControllerViewModel"/>, which checks <see cref="ControllerViewModel.CanEdit"/>.
/// </summary>
public sealed partial class ControllerSettingsViewModel : ObservableObject, IDisposable
{
    // Dimmed to: Low and Medium (the firmware's default is Low).
    private const uint Low = 8;
    private const uint Medium = 48;

    private static readonly DurationChoice[] Durations =
    {
        new("Never", null), new("1 minute", 60), new("2 minutes", 120), new("5 minutes", 300),
        new("10 minutes", 600), new("30 minutes", 1800), new("1 hour", 3600),
    };

    // Slider smoothing pairs: filter weight % and deadband (Normal = the proto defaults).
    private static readonly (uint Weight, uint Deadband)[] Feels = { (40, 15), (20, 25), (10, 40) };

    private readonly ControllerViewModel _controller;
    private readonly Action _openWifi;

    /// <param name="openBuses">Opens the I2C buses dialog.</param>
    public ControllerSettingsViewModel(ControllerViewModel controller, Action openWifi, Action openBuses)
    {
        _controller = controller;
        _openWifi = openWifi;
        _screensOpen = ShowScreens;
        _isSlidersOpen = !ShowScreens;
        OpenWifiCommand = new RelayCommand(_openWifi);
        OpenBusesCommand = new RelayCommand(openBuses);
        RefreshBuses();
        _controller.PropertyChanged += OnControllerChanged;
    }

    /// <summary>The controller's logic; the Fine tune boxes bind to its raw fields.</summary>
    public ControllerViewModel Controller => _controller;

    public string Title => "Controller";

    /// <summary>The draft can be edited: the rows are enabled.</summary>
    public bool IsEditable => _controller.CanEdit;

    // ── Sections ─────────────────────────────────────────────────────────────────────────────

    // Each section opens and closes on its own (user, 2026-10-22): Screens, Sliders and Buttons can be
    // open together. Screens starts open, Sliders only when there is no Screens section.
    private bool _screensOpen;

    /// <summary>Screens is open (never while no button has a screen).</summary>
    public bool IsScreensOpen
    {
        get => ShowScreens && _screensOpen;
        set
        {
            if (_screensOpen == value) return;
            _screensOpen = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Sliders is open.</summary>
    [ObservableProperty] private bool _isSlidersOpen;

    /// <summary>Buttons is open.</summary>
    [ObservableProperty] private bool _isButtonsOpen;

    [RelayCommand] private void ToggleScreens() => IsScreensOpen = !IsScreensOpen;
    [RelayCommand] private void ToggleSliders() => IsSlidersOpen = !IsSlidersOpen;
    [RelayCommand] private void ToggleButtons() => IsButtonsOpen = !IsButtonsOpen;
    [RelayCommand] private void ToggleScreensFine() => IsScreensFineOpen = !IsScreensFineOpen;
    [RelayCommand] private void ToggleSlidersFine() => IsSlidersFineOpen = !IsSlidersFineOpen;
    [RelayCommand] private void ToggleButtonsFine() => IsButtonsFineOpen = !IsButtonsFineOpen;

    /// <summary>Fine tune of the Screens section is open.</summary>
    [ObservableProperty] private bool _isScreensFineOpen;

    /// <summary>Fine tune of the Sliders section is open.</summary>
    [ObservableProperty] private bool _isSlidersFineOpen;

    /// <summary>Fine tune of the Buttons section is open.</summary>
    [ObservableProperty] private bool _isButtonsFineOpen;

    /// <summary>A button has a screen: the Screens section exists.</summary>
    public bool ShowScreens => _controller.ShowBehaviour;

    // ── Screens ──────────────────────────────────────────────────────────────────────────────

    /// <summary>"80%, off after 30 min".</summary>
    public string ScreensSummary
    {
        get
        {
            var text = $"{BrightnessPct}%";
            if (_controller.OffEnabled) text += $", off after {Short(Seconds(_controller.OffAfterS))}";
            return text;
        }
    }

    /// <summary>The screens' brightness in percent (the field is 0-255).</summary>
    public int BrightnessPct
    {
        get => (int)Math.Round((double)(_controller.Brightness ?? 255) * 100 / 255, MidpointRounding.AwayFromZero);
        set
        {
            var raw = (decimal)Math.Round(Math.Clamp(value, 0, 100) * 255 / 100.0, MidpointRounding.AwayFromZero);
            if (raw == _controller.Brightness) return;
            _controller.Brightness = raw;
        }
    }

    /// <summary><see cref="BrightnessPct"/> as the slider's value.</summary>
    public double BrightnessSlider
    {
        get => BrightnessPct;
        set => BrightnessPct = (int)Math.Round(value);
    }

    // Each list is one instance until its contents change, so an unrelated draft change doesn't hand
    // a combo box a new ItemsSource (which would reset its selection).
    private IReadOnlyList<DurationChoice> _dimAfter = Durations;
    private IReadOnlyList<DurationChoice> _offAfter = Durations;
    private IReadOnlyList<string> _dimmedTo = new[] { "Low", "Medium" };

    public IReadOnlyList<DurationChoice> DimAfter
        => _dimAfter = Cached(_dimAfter, _controller.DimEnabled, Seconds(_controller.DimAfterS));
    public IReadOnlyList<DurationChoice> OffAfter
        => _offAfter = Cached(_offAfter, _controller.OffEnabled, Seconds(_controller.OffAfterS));

    /// <summary>The selected entry of <see cref="DimAfter"/>.</summary>
    public int DimAfterIndex
    {
        get => IndexOf(DimAfter, _controller.DimEnabled, Seconds(_controller.DimAfterS));
        set
        {
            var list = DimAfter;
            if (value < 0 || value >= list.Count || value == DimAfterIndex || list[value].IsCustom) return;
            if (list[value].Seconds is { } s) { _controller.DimEnabled = true; _controller.DimAfterS = s; }
            else _controller.DimEnabled = false;
        }
    }

    /// <summary>The selected entry of <see cref="OffAfter"/>.</summary>
    public int OffAfterIndex
    {
        get => IndexOf(OffAfter, _controller.OffEnabled, Seconds(_controller.OffAfterS));
        set
        {
            var list = OffAfter;
            if (value < 0 || value >= list.Count || value == OffAfterIndex || list[value].IsCustom) return;
            if (list[value].Seconds is { } s) { _controller.OffEnabled = true; _controller.OffAfterS = s; }
            else _controller.OffEnabled = false;
        }
    }

    /// <summary>Low, Medium and, for another brightness, "Custom (n)".</summary>
    public IReadOnlyList<string> DimmedTo
    {
        get
        {
            var wanted = Level is Low or Medium ? null : $"Custom ({Level})";
            if (_dimmedTo.Count == (wanted is null ? 2 : 3) && (wanted is null || _dimmedTo[2] == wanted)) return _dimmedTo;
            return _dimmedTo = wanted is null ? new[] { "Low", "Medium" } : new[] { "Low", "Medium", wanted };
        }
    }

    private uint Level => (uint)(_controller.DimBrightness ?? Low);

    public int DimmedToIndex
    {
        get => Level switch { Low => 0, Medium => 1, _ => 2 };
        set
        {
            if (value is not (0 or 1) || value == DimmedToIndex) return;
            _controller.DimBrightness = value == 0 ? Low : Medium;
        }
    }

    /// <summary>Pixel shift: moves the picture a little now and then so no pixel burns in.</summary>
    public bool PreventBurnIn
    {
        get => _controller.PixelShiftEnabled;
        set => _controller.PixelShiftEnabled = value;
    }

    /// <summary>The first press after the screen turned off only wakes it (a sub-option of
    /// Turn off after; a dimmed screen passes the press on).</summary>
    public bool FirstPressOnlyWakes
    {
        get => _controller.WakeSwallowsPress;
        set => _controller.WakeSwallowsPress = value;
    }

    /// <summary>The screens turn off (Turn off after isn't Never): only then does
    /// <see cref="FirstPressOnlyWakes"/> apply.</summary>
    public bool CanSetFirstPressOnlyWakes => _controller.OffEnabled;

    private static uint Seconds(decimal? value) => (uint)(value ?? 0);

    private static IReadOnlyList<DurationChoice> Cached(IReadOnlyList<DurationChoice> current, bool enabled, uint seconds)
    {
        if (!enabled || Durations.Any(d => d.Seconds == seconds)) return Durations;
        if (current.Count > Durations.Length && current[^1].Seconds == seconds) return current;
        return Durations.Append(new DurationChoice($"Custom ({Long(seconds)})", seconds, IsCustom: true)).ToArray();
    }

    private static int IndexOf(IReadOnlyList<DurationChoice> list, bool enabled, uint seconds)
    {
        if (!enabled) return 0;
        for (int i = 1; i < list.Count; i++) if (list[i].Seconds == seconds) return i;
        return list.Count - 1;
    }

    /// <summary>"90 s", "5 min", "2 h": the summary's and the custom entry's wording.</summary>
    private static string Long(uint s) => s % 3600 == 0 && s > 0 ? $"{s / 3600} h"
        : s % 60 == 0 && s > 0 ? $"{s / 60} min" : $"{s} s";

    private static string Short(uint s) => s == 3600 ? "1 hour" : Long(s);

    // ── I2C buses (controller-page spec §4.3) ────────────────────────────────────────────────

    private readonly HashSet<int> _problemBuses = new();

    /// <summary>"Buses 0 and 2", "Bus 0", "None"; or, while one has a problem, "Bus 0 needs
    /// attention" (<see cref="BusesHaveProblem"/>).</summary>
    [ObservableProperty] private string _busesSummary = "";

    /// <summary>The draft has a problem with a bus: the summary is red.</summary>
    [ObservableProperty] private bool _busesHaveProblem;

    /// <summary>The I2C buses row's Edit: opens the I2C buses dialog.</summary>
    public IRelayCommand OpenBusesCommand { get; }

    /// <summary>Follows the draft: the buses row's summary; a bus that gains a problem opens the
    /// Screens section (once per problem), where the row is.</summary>
    internal void RefreshBuses()
    {
        var listed = I2cBusesViewModel.Listed(_controller.Draft);
        var problems = listed.Where(bus => _controller.Problems.Any(p => p.Item == bus
            && p.Field is ConfigField.I2cMissing or ConfigField.I2cPins or ConfigField.I2cClock)).ToList();
        BusesHaveProblem = problems.Count > 0;
        BusesSummary = problems.Count > 0 ? $"Bus {problems[0]} needs attention"
            : listed.Count == 0 ? "None"
            : listed.Count == 1 ? $"Bus {listed[0]}"
            : $"Buses {BusWiringViewModel.Join(listed.Select(b => $"{b}").ToList())}";
        foreach (var bus in problems)
            if (_problemBuses.Add(bus)) IsScreensOpen = true;
        _problemBuses.IntersectWith(problems);
    }

    // ── Sliders ──────────────────────────────────────────────────────────────────────────────

    /// <summary>"Low", "Normal", "High" or "Custom".</summary>
    public string SlidersSummary => SmoothingName(_controller);

    private static readonly string[] FeelNames = { "Low", "Normal", "High", "Custom" };

    /// <summary>The smoothing's name for the draft's weight and deadband: the one place that maps them
    /// (the Sliders summary and a slider's Smoothing link read it).</summary>
    internal static string SmoothingName(ControllerViewModel c) => FeelNames[FeelOf(c)];

    private static int FeelOf(ControllerViewModel c)
    {
        for (int i = 0; i < Feels.Length; i++)
            if (c.SmoothingPct == Feels[i].Weight && c.Deadband == Feels[i].Deadband) return i;
        return 3;
    }

    /// <summary>0 Low, 1 Normal, 2 High, 3 Custom (the weight and deadband match none of the pairs).
    /// Debounce is not touched.</summary>
    public int InputFeel
    {
        get => FeelOf(_controller);
        set
        {
            if (value is < 0 or > 2 || !_controller.CanEdit)
            {
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsFeelLow));
                OnPropertyChanged(nameof(IsFeelNormal));
                OnPropertyChanged(nameof(IsFeelHigh));
                return;
            }
            _controller.SmoothingPct = Feels[value].Weight;
            _controller.Deadband = Feels[value].Deadband;
        }
    }

    public bool IsFeelLow { get => InputFeel == 0; set { if (value) InputFeel = 0; } }
    public bool IsFeelNormal { get => InputFeel == 1; set { if (value) InputFeel = 1; } }
    public bool IsFeelHigh { get => InputFeel == 2; set { if (value) InputFeel = 2; } }
    public bool IsFeelCustom => InputFeel == 3;

    // ── Buttons ──────────────────────────────────────────────────────────────────────────────

    /// <summary>"500 ms · 300 ms": Hold after and Double press within.</summary>
    public string ButtonsSummary => $"{_controller.HoldMs} ms · {_controller.DoublePressMs} ms";

    /// <summary>Hold after (ms), a pass-through of the timing draft.</summary>
    public decimal? HoldMs
    {
        get => _controller.HoldMs;
        set => _controller.HoldMs = value;
    }

    /// <summary>Double press within (ms), a pass-through of the timing draft.</summary>
    public decimal? DoublePressMs
    {
        get => _controller.DoublePressMs;
        set => _controller.DoublePressMs = value;
    }

    // ── Wi-Fi ────────────────────────────────────────────────────────────────────────────────

    /// <summary>"Off" or "On · HomeNet".</summary>
    public string WifiSummary => !_controller.UdpEnabled ? "Off"
        : string.IsNullOrEmpty(_controller.Ssid) ? "On" : $"On · {_controller.Ssid}";

    /// <summary>Opens the Connection dialog on Wi-Fi.</summary>
    public IRelayCommand OpenWifiCommand { get; }

    // ── Keeping current ──────────────────────────────────────────────────────────────────────

    private static readonly string[] Watched =
    {
        nameof(IsEditable), nameof(ShowScreens), nameof(IsScreensOpen), nameof(ScreensSummary),
        nameof(BrightnessPct), nameof(BrightnessSlider), nameof(DimAfter), nameof(DimAfterIndex), nameof(OffAfter),
        nameof(OffAfterIndex), nameof(DimmedTo), nameof(DimmedToIndex), nameof(PreventBurnIn), nameof(FirstPressOnlyWakes),
        nameof(CanSetFirstPressOnlyWakes),
        nameof(SlidersSummary), nameof(ButtonsSummary), nameof(HoldMs), nameof(DoublePressMs), nameof(InputFeel), nameof(IsFeelLow), nameof(IsFeelNormal), nameof(IsFeelHigh),
        nameof(IsFeelCustom), nameof(WifiSummary),
    };

    private void OnControllerChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Any draft or state change can move a summary, a list's custom entry or the feel.
        RefreshBuses();
        foreach (var name in Watched) OnPropertyChanged(name);
        // The last screen went while Screens was open: Sliders opens, so something stays open
        if (!ShowScreens && _screensOpen)
        {
            _screensOpen = false;
            IsSlidersOpen = true;
        }
    }

    public void Dispose() => _controller.PropertyChanged -= OnControllerChanged;
}
