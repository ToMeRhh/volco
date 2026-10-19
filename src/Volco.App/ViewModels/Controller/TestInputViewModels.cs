using CommunityToolkit.Mvvm.ComponentModel;
using Volco.Core.Communication;
using Volco.Core.Runtime;

namespace Volco.App.ViewModels;

/// <summary>A slider in the test dialog: its raw value and a bar.</summary>
public sealed partial class TestSliderViewModel(int index) : ObservableObject
{
    public int Index { get; } = index;
    public string Title => $"Slider {Index + 1}";

    [ObservableProperty] private int _value;

    /// <summary>0–1 for the bar.</summary>
    public double Level => Value / 4095.0;

    partial void OnValueChanged(int value) => OnPropertyChanged(nameof(Level));
}

/// <summary>A button in the test dialog: a lamp while pressed, and what its indicator shows now.
/// The runtime moves the indicator on with each press (<see cref="TestMode"/>).</summary>
public sealed partial class TestButtonViewModel(int index, IndicatorKind kind) : ObservableObject
{
    public int Index { get; } = index;
    public IndicatorKind Kind { get; } = kind;
    public string Title => $"Button {Index + 1}";

    [ObservableProperty] private bool _isPressed;

    /// <summary>Presses so far in this test.</summary>
    [ObservableProperty] private int _presses;

    partial void OnPressesChanged(int value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsLit));
    }

    /// <summary>"LED off" / "LED on", "LED A" / "LED B", "Test screen" / "Screen: 3 presses".</summary>
    public string StateText => Kind switch
    {
        IndicatorKind.Led => TestMode.Indicator(Kind, Presses) == TestIndicator.On ? "LED on" : "LED off",
        IndicatorKind.LedPair => TestMode.Indicator(Kind, Presses) switch
        {
            TestIndicator.A => "LED A",
            TestIndicator.B => "LED B",
            _ => "LEDs off",
        },
        IndicatorKind.Screen => Presses == 0 ? "Test screen" : $"Screen: {TestMode.PressText(Presses)}",
        _ => Presses == 0 ? "No light" : TestMode.PressText(Presses),
    };

    /// <summary>An LED is lit on the controller now.</summary>
    public bool IsLit => TestMode.Indicator(Kind, Presses) != TestIndicator.Off;

    internal void Reset()
    {
        Presses = 0;
        IsPressed = false;
    }
}
