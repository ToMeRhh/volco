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
/// The Hardware inspector of the controller's settings (hardware-page spec §6.6): Screens, Input feel
/// and Wi-Fi, one open at a time, in plain words over the draft's raw fields (which stay under
/// Fine tune). A value off a list shows as "Custom (90 s)" and is kept. Every edit goes through
/// <see cref="ControllerViewModel"/>, which checks <see cref="ControllerViewModel.CanEdit"/>.
/// </summary>
public sealed partial class ControllerSettingsViewModel : ObservableObject, IDisposable
{
    public const int ScreensSection = 0;
    public const int InputSection = 1;
    public const int WifiSection = 2;

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
    private readonly Func<PinOwner, PinFieldViewModel> _pin;

    /// <param name="pin">The pin field of a use (a bus's SDA or SCL), shared with the rest of the page.</param>
    public ControllerSettingsViewModel(ControllerViewModel controller, Action openWifi, Func<PinOwner, PinFieldViewModel> pin)
    {
        _controller = controller;
        _openWifi = openWifi;
        _pin = pin;
        _openSection = ShowScreens ? ScreensSection : InputSection;
        RefreshBuses();
        OpenWifiCommand = new RelayCommand(_openWifi);
        _controller.PropertyChanged += OnControllerChanged;
    }

    /// <summary>The controller's logic; the Fine tune boxes bind to its raw fields.</summary>
    public ControllerViewModel Controller => _controller;

    public string Title => "Controller";

    /// <summary>The draft can be edited: the rows are enabled.</summary>
    public bool IsEditable => _controller.CanEdit;

    // ── Sections ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The open section (<see cref="ScreensSection"/>, <see cref="InputSection"/>,
    /// <see cref="WifiSection"/>), or -1 for none; opening one closes the others.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScreensOpen), nameof(IsInputOpen))]
    private int _openSection;

    public bool IsScreensOpen => ShowScreens && OpenSection == ScreensSection;
    public bool IsInputOpen => OpenSection == InputSection;

    /// <summary>A header's click: opens its section, or closes it when it is the open one.</summary>
    public void ToggleSection(int section) => OpenSection = OpenSection == section ? -1 : section;

    [RelayCommand] private void ToggleScreens() => ToggleSection(ScreensSection);
    [RelayCommand] private void ToggleInput() => ToggleSection(InputSection);
    [RelayCommand] private void ToggleScreensFine() => IsScreensFineOpen = !IsScreensFineOpen;
    [RelayCommand] private void ToggleInputFine() => IsInputFineOpen = !IsInputFineOpen;

    /// <summary>Fine tune of the Screens section is open.</summary>
    [ObservableProperty] private bool _isScreensFineOpen;

    /// <summary>Fine tune of the Input feel section is open.</summary>
    [ObservableProperty] private bool _isInputFineOpen;

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

    private IReadOnlyList<BusWiringViewModel> _buses = [];
    private readonly HashSet<int> _problemBuses = new();

    /// <summary>One row per I2C bus a screen uses, in bus order, in the Screens section.</summary>
    public IReadOnlyList<BusWiringViewModel> Buses => _buses;

    /// <summary>Opens Screens and bus <paramref name="bus"/>'s row (the button's bus link).</summary>
    public void OpenBus(int bus)
    {
        if (!ShowScreens) return;
        OpenSection = ScreensSection;
        if (_buses.FirstOrDefault(b => b.Bus == bus) is { } row) row.IsOpen = true;
    }

    /// <summary>Follows the draft: a row per bus a screen uses, each re-read; a bus that gains a
    /// problem opens its row (once per problem) and the Screens section.</summary>
    internal void RefreshBuses()
    {
        var used = _controller.Draft.Buttons.Where(b => b.IndicatorCase == Button.IndicatorOneofCase.Screen)
                              .Select(b => (int)b.Screen.Bus).Where(b => b <= 3).Distinct().Order().ToList();
        if (!_buses.Select(b => b.Bus).SequenceEqual(used))
        {
            var old = _buses.ToDictionary(b => b.Bus);
            _buses = used.Select(bus => old.TryGetValue(bus, out var row) ? row
                : new BusWiringViewModel(bus, _controller, _pin(new PinOwner(PinOwnerKind.BusSda, bus)),
                                         _pin(new PinOwner(PinOwnerKind.BusScl, bus)))).ToList();
            OnPropertyChanged(nameof(Buses));
        }
        foreach (var row in _buses)
        {
            row.Refresh();
            if (!row.HasProblem) _problemBuses.Remove(row.Bus);
            else if (_problemBuses.Add(row.Bus)) { row.IsOpen = true; OpenSection = ScreensSection; }
        }
        _problemBuses.IntersectWith(used);
    }

    // ── Input feel ───────────────────────────────────────────────────────────────────────────

    /// <summary>"Low", "Normal", "High" or "Custom".</summary>
    public string InputSummary => FeelNames[InputFeel];

    private static readonly string[] FeelNames = { "Low", "Normal", "High", "Custom" };

    /// <summary>0 Low, 1 Normal, 2 High, 3 Custom (the weight and deadband match none of the pairs).
    /// Debounce and the sample interval are not touched.</summary>
    public int InputFeel
    {
        get
        {
            for (int i = 0; i < Feels.Length; i++)
                if (_controller.SmoothingPct == Feels[i].Weight && _controller.Deadband == Feels[i].Deadband) return i;
            return 3;
        }
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
        nameof(InputSummary), nameof(InputFeel), nameof(IsFeelLow), nameof(IsFeelNormal), nameof(IsFeelHigh),
        nameof(IsFeelCustom), nameof(WifiSummary),
    };

    private void OnControllerChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Any draft or state change can move a summary, a list's custom entry or the feel.
        RefreshBuses();
        foreach (var name in Watched) OnPropertyChanged(name);
        if (!ShowScreens && OpenSection == ScreensSection) OpenSection = InputSection;
    }

    public void Dispose() => _controller.PropertyChanged -= OnControllerChanged;
}
