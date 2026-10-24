using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Communication;
using Volco.Core.Runtime;

namespace Volco.App.ViewModels.Hardware;

/// <summary>
/// The Re-calibrate sweep (slider-calibration spec, "The sweep"): made when the dialog opens, it
/// holds the slider's input away from audio and starts a sweep on the controller; the bar follows
/// the reported ends. Done (once the span is learned) and Cancel end the sweep; the dialog closes
/// on the controller's ending report (else after <see cref="CancelTimeout"/>), and when the
/// controller is no longer ready. Disposing it cancels a sweep that hasn't ended and always
/// releases the hold. Runtime events are posted to the UI thread; everything else runs there.
/// </summary>
public sealed partial class CalibrationDialogViewModel : ObservableObject, IDisposable
{
    private const double MaxRaw = 4095.0;

    /// <summary>How long Done or Cancel waits for the controller's ending report before the dialog closes anyway.</summary>
    internal static TimeSpan CancelTimeout { get; set; } = TimeSpan.FromSeconds(2);

    private readonly int _slider;
    private readonly bool _reversed;
    private readonly VolcoRuntime _runtime;
    private readonly IUiDispatcher _ui;
    private readonly Action _close;
    private IDisposable? _hold;
    private bool _sawSweep;
    private bool _ending;
    private bool _ended;
    private bool _closed;
    private bool _disposed;

    /// <param name="sliderIndex">The slider's hardware input (0-based).</param>
    /// <param name="number">The slider's number in the drawing ("Slider N"); defaults to the input's.</param>
    /// <param name="reversed">The slider is reversed on the controller (its saved setting): the bar is mirrored
    /// so it moves with the slider; the reported ends, and their captions, stay the raw readings.</param>
    public CalibrationDialogViewModel(int sliderIndex, VolcoRuntime runtime, IUiDispatcher ui, Action close, int? number = null,
                                      bool reversed = false)
    {
        _slider = sliderIndex;
        _reversed = reversed;
        _runtime = runtime;
        _ui = ui;
        _close = close;
        Title = $"Calibrate Slider {number ?? sliderIndex + 1}";
        DoneCommand = new RelayCommand(Done, () => CanDone);
        CancelCommand = new RelayCommand(Cancel);
        _runtime.CalibrationChanged += OnCalibrationChanged;
        _runtime.ControllerStateChanged += OnControllerStateChanged;
        _hold = _runtime.HoldSlider(sliderIndex);
        if (_runtime.ControllerState != ControllerState.Ready || !_runtime.SendCalibrate(sliderIndex, CalibrateAction.Start))
            _ui.Post(Close);   // nothing to sweep with: the dialog goes
    }

    /// <summary>The slider's hardware input (0-based).</summary>
    public int SliderIndex => _slider;

    /// <summary>"Calibrate Slider N".</summary>
    public string Title { get; }

    public string Instruction => "Move the slider all the way to both ends, then click Done";

    /// <summary>The learned low end, 0-1 of the raw range (0 until the sweep reports a range).</summary>
    [ObservableProperty] private double _low;

    /// <summary>The learned high end, 0-1 of the raw range.</summary>
    [ObservableProperty] private double _high;

    /// <summary>The sweep has reported a range (its high end is at or above its low end).</summary>
    [ObservableProperty] private bool _hasRange;

    /// <summary>The low end's raw value, or "—" before a range.</summary>
    [ObservableProperty] private string _lowText = "—";

    /// <summary>The high end's raw value, or "—" before a range.</summary>
    [ObservableProperty] private string _highText = "—";

    /// <summary>The sweep's span is learned: Done is enabled.</summary>
    [ObservableProperty] private bool _isLearned;

    /// <summary>Done is enabled: the reported span is learned and the sweep isn't ending.</summary>
    public bool CanDone => IsLearned && !_ending;

    /// <summary>Shown while the span isn't learned.</summary>
    public string KeepMovingText => "Keep moving: the travel is too short so far";

    /// <summary>Done or Cancel was sent: waiting for the controller's ending report.</summary>
    public bool IsEnding => _ending;

    /// <summary>Ends the sweep and stores the ends; the dialog closes on the ending report, else
    /// after <see cref="CancelTimeout"/> (closing then cancels a sweep the controller didn't end).</summary>
    public IRelayCommand DoneCommand { get; }

    /// <summary>Cancel, Escape, the scrim: the controller restores the ends it had; the dialog closes
    /// on the ending report, else after <see cref="CancelTimeout"/>.</summary>
    public IRelayCommand CancelCommand { get; }

    partial void OnIsLearnedChanged(bool value) => RaiseDone();

    private void RaiseDone()
    {
        OnPropertyChanged(nameof(CanDone));
        DoneCommand.NotifyCanExecuteChanged();
    }

    private void Done()
    {
        if (_disposed || !CanDone) return;
        SetEnding();
        if (!_runtime.SendCalibrate(_slider, CalibrateAction.Done))
        {
            Close();
            return;
        }
        CloseAfterTimeout();
    }

    private void Cancel()
    {
        if (_disposed || _ended) return;
        SetEnding();
        if (!_runtime.SendCalibrate(_slider, CalibrateAction.Cancel))
        {
            Close();
            return;
        }
        _ended = true;   // the controller was told: disposing needn't cancel again
        CloseAfterTimeout();
    }

    private void CloseAfterTimeout() => _ = Task.Delay(CancelTimeout).ContinueWith(_ => _ui.Post(Close), TaskScheduler.Default);

    private void SetEnding()
    {
        _ending = true;
        OnPropertyChanged(nameof(IsEnding));
        RaiseDone();
    }

    private void OnCalibrationChanged(object? sender, (int Slider, SliderCalibrationState? State) e)
    {
        // A null state is a Hello forgetting the last report: not news about this sweep.
        if (e.Slider != _slider || e.State is not { } state) return;
        _ui.Post(() => OnReport(state));
    }

    private void OnReport(SliderCalibrationState state)
    {
        if (_disposed) return;
        if (state.Sweeping)
        {
            _sawSweep = true;
            Show(state);
            return;
        }
        // A report from before the sweep began (an auto-calibration widening in flight) isn't its end.
        if (!_sawSweep && !_ending) return;
        _ended = true;
        Close();
    }

    private void Show(SliderCalibrationState state)
    {
        HasRange = state.High >= state.Low;
        var low = Math.Clamp(state.Low / MaxRaw, 0, 1);
        var high = Math.Clamp(state.High / MaxRaw, 0, 1);
        Low = HasRange ? (_reversed ? 1 - high : low) : 0;
        High = HasRange ? (_reversed ? 1 - low : high) : 0;
        // The bar is mirrored for a reversed slider, so its left caption is the raw high (values stay raw).
        LowText = HasRange ? $"{(_reversed ? state.High : state.Low)}" : "—";
        HighText = HasRange ? $"{(_reversed ? state.Low : state.High)}" : "—";
        IsLearned = state.Learned;
    }

    private void OnControllerStateChanged(object? sender, ControllerState state) => _ui.Post(() =>
    {
        // The link dropped: the controller cancels the sweep itself (host lost); the banner says why.
        if (!_disposed && _runtime.ControllerState != ControllerState.Ready)
        {
            _ended = true;
            Close();
        }
    });

    /// <summary>Releases the hold and asks the owner to close the dialog (which disposes it).</summary>
    private void Close()
    {
        if (_closed) return;
        _closed = true;
        ReleaseHold();
        _close();
        Dispose();
    }

    private void ReleaseHold()
    {
        var hold = _hold;
        _hold = null;
        hold?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runtime.CalibrationChanged -= OnCalibrationChanged;
        _runtime.ControllerStateChanged -= OnControllerStateChanged;
        // Closed before the sweep ended (the window hidden, the page torn down, no answer to Done):
        // the controller is told to cancel it (after a Done it already took, this changes nothing).
        if (!_ended) _runtime.SendCalibrate(_slider, CalibrateAction.Cancel);
        ReleaseHold();
    }
}
