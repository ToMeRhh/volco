using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Communication;
using Volco.Core.Runtime;

namespace Volco.App.ViewModels.Hardware;

/// <summary>
/// A tile's captions on Hardware (spec docs/design/hardware-page.md §5.2), shown instead of its
/// actions: its name, its pin, its indicator's wiring, the draft-change dot, a problem and, on a
/// slider, the live raw level. During the hardware test (spec §8) it also carries the test state:
/// the raw value or the button's light, its presses and whether it is held.
/// </summary>
public sealed partial class TileWiring : ObservableObject
{
    /// <summary>"Slider N" or "Button N".</summary>
    [ObservableProperty]
    private string _title = "";

    /// <summary>The pin, as its pill reads ("GPIO 33").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPin))]
    private string _pinText = "";

    /// <summary>A button's indicator wiring ("12", "18 · 19", "Bus 0 · 3C"); empty for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowIndicatorText))]
    private string _indicatorText = "";

    /// <summary>The pin pill shows: there is a pin text and no test runs.</summary>
    public bool ShowPin => !IsTesting && PinText.Length > 0;

    /// <summary>The indicator's wiring shows: there is one and no test runs.</summary>
    public bool ShowIndicatorText => !IsTesting && IndicatorText.Length > 0;

    /// <summary>The draft changes this control (the dot, until saved).</summary>
    [ObservableProperty]
    private bool _hasChange;

    /// <summary>The draft's problem with this control (the danger outline and its tooltip), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string? _problem;

    public bool HasProblem => Problem is not null;

    /// <summary>A slider's live raw level, 0..1.</summary>
    [ObservableProperty]
    private double _rawLevel;

    // ── The hardware test (spec §8) ──────────────────────────────────────────────────────────

    /// <summary>A hardware test runs: the tile shows its test state instead of its pin.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPin), nameof(ShowIndicatorText), nameof(IsTestLed), nameof(IsTestPair),
                              nameof(IsTestScreen), nameof(HasTestLight))]
    private bool _isTesting;

    /// <summary>A slider's raw value ("2310"); a button's state ("On", "Off", "A", "B", "3 presses").</summary>
    [ObservableProperty]
    private string _testText = "";

    /// <summary>The button is held down (during a test).</summary>
    [ObservableProperty]
    private bool _isPressed;

    /// <summary>The button's indicator on the controller (from its Hello), or null when the
    /// controller doesn't have this control.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTestLed), nameof(IsTestPair), nameof(IsTestScreen), nameof(HasTestLight))]
    private IndicatorKind? _testKind;

    /// <summary>What the light shows in the test.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestLedOn), nameof(TestLedA), nameof(TestLedB))]
    private TestIndicator _testLight;

    /// <summary>The test screen a screen button shows (<see cref="TestMode.Screen"/>).</summary>
    [ObservableProperty]
    private ScreenContent? _testScreen;

    public bool IsTestLed => IsTesting && TestKind == IndicatorKind.Led;
    public bool IsTestPair => IsTesting && TestKind == IndicatorKind.LedPair;
    public bool IsTestScreen => IsTesting && TestKind == IndicatorKind.Screen;

    /// <summary>The light can be clicked to switch it (a single LED or a pair).</summary>
    public bool HasTestLight => IsTestLed || IsTestPair;

    public bool TestLedOn => TestLight is TestIndicator.On or TestIndicator.A;
    public bool TestLedA => TestLight == TestIndicator.A;
    public bool TestLedB => TestLight == TestIndicator.B;

    /// <summary>What a click on the light does (moves it on as one press would); set by the page.</summary>
    internal Action? Advance { get; set; }

    /// <summary>A click on the test light.</summary>
    [RelayCommand]
    private void AdvanceLight() => Advance?.Invoke();
}
