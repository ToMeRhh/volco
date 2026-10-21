using CommunityToolkit.Mvvm.ComponentModel;

namespace Volco.App.ViewModels.Hardware;

/// <summary>
/// Which sections of the combined inspector are open (controller-page spec §4.3), remembered for
/// the app run per kind: opening a slider's Hardware once keeps it open for the next slider, so
/// rewiring several controls doesn't reopen it each time. A Hardware section whose control gets a
/// problem opens itself (<see cref="HardwareViewModel"/>).
/// </summary>
public sealed partial class InspectorSections : ObservableObject
{
    /// <summary>A slider's Controls section (what it controls, Response).</summary>
    [ObservableProperty] private bool _sliderControlsOpen = true;

    /// <summary>A slider's Hardware section (its pin, Remove).</summary>
    [ObservableProperty] private bool _sliderHardwareOpen;

    /// <summary>A button's Actions section (the gestures, the light row, Timing).</summary>
    [ObservableProperty] private bool _buttonActionsOpen = true;

    /// <summary>A button's Hardware section (its pin, its light's wiring, Remove).</summary>
    [ObservableProperty] private bool _buttonHardwareOpen;
}
