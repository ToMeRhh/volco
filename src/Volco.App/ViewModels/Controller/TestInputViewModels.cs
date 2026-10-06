using CommunityToolkit.Mvvm.ComponentModel;
using Volco.Core.Communication;
using Volco.Core.Runtime;

namespace Volco.App.ViewModels;

/// <summary>A slider in the Test section: its raw value and a bar.</summary>
public sealed partial class TestSliderViewModel(int index) : ObservableObject
{
    public int Index { get; } = index;
    public string Title => $"Slider {Index}";

    [ObservableProperty] private int _value;

    /// <summary>0–1 for the bar.</summary>
    public double Level => Value / 4095.0;

    partial void OnValueChanged(int value) => OnPropertyChanged(nameof(Level));
}

/// <summary>A button in the Test section: a lamp while pressed, and for LED buttons the test
/// indicator (Off/On, or Off/A/B for a pair).</summary>
public sealed partial class TestButtonViewModel : ObservableObject
{
    private readonly Action<int, TestIndicator> _setIndicator;

    public TestButtonViewModel(int index, IndicatorKind kind, Action<int, TestIndicator> setIndicator)
    {
        Index = index;
        Kind = kind;
        _setIndicator = setIndicator;
    }

    public int Index { get; }
    public IndicatorKind Kind { get; }
    public string Title => $"Button {Index}";

    public bool IsLed => Kind == IndicatorKind.Led;
    public bool IsPair => Kind == IndicatorKind.LedPair;
    public bool IsScreen => Kind == IndicatorKind.Screen;

    [ObservableProperty] private bool _isPressed;

    public static IReadOnlyList<string> LedChoices { get; } = new[] { "Off", "On" };
    public static IReadOnlyList<string> PairChoices { get; } = new[] { "Off", "A", "B" };

    private int _indicatorIndex;

    /// <summary>LED: 0 Off, 1 On. Pair: 0 Off, 1 A, 2 B.</summary>
    public int IndicatorIndex
    {
        get => _indicatorIndex;
        set
        {
            if (!SetProperty(ref _indicatorIndex, value)) return;
            _setIndicator(Index, (IsPair, value) switch
            {
                (_, 0) => TestIndicator.Off,
                (false, _) => TestIndicator.On,
                (true, 1) => TestIndicator.A,
                _ => TestIndicator.B,
            });
        }
    }

    internal void ResetIndicator()
    {
        _indicatorIndex = 0;
        OnPropertyChanged(nameof(IndicatorIndex));
        IsPressed = false;
    }
}
