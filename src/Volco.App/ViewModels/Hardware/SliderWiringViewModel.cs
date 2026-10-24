using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Controller.Proto;
using Volco.Core.Runtime;

namespace Volco.App.ViewModels.Hardware;

/// <summary>
/// The Hardware section of a slider's inspector (controller-page spec §4.3, hardware-page spec
/// §6.1): its pin, Reverse direction, Auto-calibrate and its calibration with Re-calibrate (slider-calibration spec,
/// "What the Hardware section shows"), Remove, and the danger mark on the section's header. Made
/// for one draft slider; the Hardware view model refreshes it after every draft change and drops it
/// when the selection moves.
/// </summary>
public sealed partial class SliderWiringViewModel : ObservableObject
{
    private readonly ControllerViewModel _controller;
    private readonly Action _recalibrate;

    internal SliderWiringViewModel(int index, SliderItemViewModel item, PinFieldViewModel pin, Action remove,
                                   ControllerViewModel controller, Action recalibrate, Action openSmoothing)
    {
        Index = index;
        Item = item;
        Pin = pin;
        _controller = controller;
        _recalibrate = recalibrate;
        RemoveCommand = new RelayCommand(remove);
        OpenSmoothingCommand = new RelayCommand(openSmoothing);
        _smoothingSummary = ControllerSettingsViewModel.SmoothingName(controller);
        RecalibrateCommand = new RelayCommand(() => _recalibrate(), () => CanRecalibrate);
    }

    /// <summary>The draft slider (0-based).</summary>
    public int Index { get; }

    /// <summary>The Controls item it draws.</summary>
    public SliderItemViewModel Item { get; }

    /// <summary>"Slider N".</summary>
    public string Title => $"Slider {Index + 1}";

    public PinFieldViewModel Pin { get; }

    /// <summary>"Remove Slider N".</summary>
    public string RemoveText => $"Remove {Title}";

    /// <summary>Asks "Remove Slider N?", then removes it from the draft.</summary>
    public IRelayCommand RemoveCommand { get; }

    /// <summary>The draft can be edited: Auto-calibrate and Remove are enabled.</summary>
    [ObservableProperty] private bool _isEditable;

    /// <summary>The draft has a problem with this slider: the section's danger mark.</summary>
    [ObservableProperty] private bool _hasProblem;

    // ── Calibration (slider-calibration spec) ────────────────────────────────────────────────

    private Slider? Message => Index < _controller.Draft.Sliders.Count ? _controller.Draft.Sliders[Index] : null;

    /// <summary>The slider in Volco's saved copy; null for one that isn't saved.</summary>
    private Slider? _saved;

    /// <summary>Auto-calibrate (a draft field, sent with Save like the pin; on by default).</summary>
    public bool AutoCalibrate
    {
        get => Message?.AutoCalibrate ?? true;
        set
        {
            if (Message is not { } s || value == AutoCalibrate || !_controller.CanEdit) { OnPropertyChanged(); return; }
            // Back to the saved value, the message is again the saved one byte for byte (absent = on).
            if (value && _saved is not { HasAutoCalibrate: true }) s.ClearAutoCalibrate();
            else s.AutoCalibrate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoCalibrateNote));
            _controller.OnDraftEdited();
        }
    }

    /// <summary>Reverse direction (a draft field, sent with Save like the pin; off by default): the controller
    /// sends 4095 minus the reading.</summary>
    public bool Reverse
    {
        get => Message?.Reverse ?? false;
        set
        {
            if (Message is not { } s || value == Reverse || !_controller.CanEdit) { OnPropertyChanged(); return; }
            // Back to the saved value, the message is again the saved one byte for byte (absent = off).
            if (!value && _saved is not { HasReverse: true }) s.ClearReverse();
            else s.Reverse = value;
            OnPropertyChanged();
            _controller.OnDraftEdited();
        }
    }

    /// <summary>The controller-wide smoothing's name ("Normal"), beside "Smoothing (all sliders)".</summary>
    [ObservableProperty] private string _smoothingSummary;

    /// <summary>Opens Controller settings with the Sliders card open.</summary>
    public IRelayCommand OpenSmoothingCommand { get; }

    /// <summary>The Auto-calibrate row's caption while the draft's setting differs from the saved one.</summary>
    public string? AutoCalibrateNote
        => _saved is { } saved && Message is { } s && saved.AutoCalibrate != s.AutoCalibrate
            ? "Saving forgets this slider's calibration" : null;

    /// <summary>The Calibration row's caption: what the controller last reported.</summary>
    [ObservableProperty] private string _calibrationText = "—";

    /// <summary>Re-calibrate can start a sweep: the controller is ready and runs this slider as
    /// saved (no unsaved change of it, not new), and no test, save or firmware operation runs.</summary>
    [ObservableProperty] private bool _canRecalibrate;

    /// <summary>Why Re-calibrate is off: "Save first" while the slider's unsaved changes
    /// hold it off, "Volco's setup differs from the controller's" while the controller holds another setup.</summary>
    [ObservableProperty] private string? _recalibrateTooltip;

    /// <summary>Re-calibrate: the sweep dialog.</summary>
    public IRelayCommand RecalibrateCommand { get; }

    partial void OnCanRecalibrateChanged(bool value) => RecalibrateCommand.NotifyCanExecuteChanged();

    /// <summary>The calibration rows, from the Hardware view model.</summary>
    /// <param name="saved">The slider in Volco's saved copy, or null for a new one.</param>
    /// <param name="state">The controller's last report for this slider while it is connected, else null.</param>
    /// <param name="canRecalibrate">Re-calibrate is enabled.</param>
    /// <param name="why">Why Re-calibrate is off, for its tooltip; null for no tooltip.</param>
    internal void UpdateCalibration(Slider? saved, SliderCalibrationState? state, bool canRecalibrate, string? why)
    {
        _saved = saved;
        OnPropertyChanged(nameof(AutoCalibrate));
        OnPropertyChanged(nameof(Reverse));
        OnPropertyChanged(nameof(AutoCalibrateNote));
        SmoothingSummary = ControllerSettingsViewModel.SmoothingName(_controller);
        CalibrationText = CaptionFor(state);
        CanRecalibrate = canRecalibrate;
        RecalibrateTooltip = why;
    }

    /// <summary>The spec's words for a report; "—" for none.</summary>
    internal static string CaptionFor(SliderCalibrationState? state) => state switch
    {
        null => "—",
        { Learned: true } s => $"Calibrated: {s.Low}-{s.High}",
        { AutoCalibrate: true } => "Not calibrated yet: move the slider end to end",
        _ => "Not calibrated: uses the raw reading",
    };
}
