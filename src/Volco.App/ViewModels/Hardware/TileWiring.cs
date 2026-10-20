using CommunityToolkit.Mvvm.ComponentModel;

namespace Volco.App.ViewModels.Hardware;

/// <summary>
/// A tile's captions on Hardware (spec docs/design/hardware-page.md §5.2), shown instead of its
/// actions: its name, its pin, its indicator's wiring, the draft-change dot, a problem and, on a
/// slider, the live raw level.
/// </summary>
public sealed partial class TileWiring : ObservableObject
{
    /// <summary>"Slider N" or "Button N".</summary>
    [ObservableProperty]
    private string _title = "";

    /// <summary>The pin, as its pill reads ("GPIO 33").</summary>
    [ObservableProperty]
    private string _pinText = "";

    /// <summary>A button's indicator wiring ("12", "18 · 19", "Bus 0 · 3C"); empty for none.</summary>
    [ObservableProperty]
    private string _indicatorText = "";

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
}
