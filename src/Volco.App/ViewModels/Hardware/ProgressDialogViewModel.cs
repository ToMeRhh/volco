using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Controller;
using Volco.Core.Firmware;
using Volco.Core.Runtime;

namespace Volco.App.ViewModels.Hardware;

/// <summary>The progress dialog's next step once its operation ended.</summary>
public enum ProgressNextStep { None, TestNow, TryAgain, InstallFirmware, ReinstallFirmware, ChooseHowToStart }

/// <summary>
/// The status's third level (controller-page spec §5, "Running, don't interrupt"): a modal dialog
/// over the window while a firmware install, update or check, or a save to the controller, runs
/// (the Connection dialog's Wi-Fi saves too, through <see cref="BeginWifi"/>).
/// It shows the operation's name, its step and progress and "Don't unplug the controller.", and
/// can't be closed while it writes. At the end it shows the result and its next step (Test your
/// controller now after an update or a first save, Try again after a failure, Install firmware
/// after a check that found none of Volco's) and stays until
/// Close; a plain confirmed save shows "Saved." briefly and closes itself. Closing leaves what
/// still needs the user to the banner. UI thread.
/// </summary>
public sealed partial class ProgressDialogViewModel : ObservableObject, IDisposable
{
    public const string WarningText = "Don't unplug the controller.";

    /// <summary>How long a confirmed save's "Saved." stays before the dialog closes itself.</summary>
    internal static TimeSpan SavedHold { get; set; } = TimeSpan.FromSeconds(1);

    private enum Operation { None, Install, Update, Check, Save, Wifi }

    private readonly ControllerViewModel _controller;
    private readonly Action _toController;
    private Operation _operation;
    private int _generation;
    private bool _disposed;

    /// <param name="toController">Shows the Controller page (Test your controller now belongs to the drawing).</param>
    public ProgressDialogViewModel(ControllerViewModel controller, Action toController)
    {
        _controller = controller;
        _toController = toController;
        _controller.PropertyChanged += OnControllerChanged;
        _controller.FirmwareFinished += OnFirmwareFinished;
        _controller.SaveFinished += OnSaveFinished;
        Update();
    }

    /// <summary>The dialog is over the window: an operation runs, or its result waits for Close.</summary>
    [ObservableProperty] private bool _isOpen;

    /// <summary>The operation still runs: nothing closes the dialog.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    private bool _isRunning;

    /// <summary>The operation's name: "Installing firmware", "Updating firmware", "Checking firmware"
    /// or "Saving to the controller".</summary>
    [ObservableProperty] private string _title = "";

    /// <summary>The step the operation is on ("Writing the firmware… 62%", "Sending…", "Restarting…").</summary>
    [ObservableProperty] private string? _step;

    /// <summary>The write's fraction (0–1); null when the operation has no measure (the bar runs
    /// without one).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMeasure))]
    private double? _progress;

    public string Warning => WarningText;

    /// <summary>The operation ended: the result shows.</summary>
    public bool IsDone => IsOpen && !IsRunning && !IsAskingPort;

    partial void OnIsOpenChanged(bool value) => OnPropertyChanged(nameof(IsDone));

    /// <summary>A firmware operation waits to be told which port the controller is on (Automatic
    /// couldn't tell, spec §10.2): the dialog lists the ports; Close abandons it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    private bool _isAskingPort;

    /// <summary>The ports to pick from; each one runs the operation on it.</summary>
    public IReadOnlyList<PortChoice> PortChoices => _controller.FirmwarePortChoices;

    /// <summary>Picks a port: the operation starts on it (the dialog then follows the run).</summary>
    public System.Windows.Input.ICommand ChoosePortCommand => _controller.ChooseFirmwarePortCommand;

    public bool HasMeasure => Progress is not null;

    [ObservableProperty] private string? _resultText;

    [ObservableProperty] private LinkTone _resultTone;

    /// <summary>The next step after the result: the test (after an update or a first save), the
    /// same operation again (after a failure), or Install firmware (a check found no Volco firmware).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextLabel), nameof(HasNext))]
    private ProgressNextStep _nextStep;

    /// <summary>The next step's button: "Test your controller now", "Try again", "Install firmware",
    /// "Reinstall firmware…", "Choose how to start", or null for none.</summary>
    public string? NextLabel => NextStep switch
    {
        ProgressNextStep.TestNow => "Test your controller now",
        ProgressNextStep.TryAgain => "Try again",
        ProgressNextStep.InstallFirmware => "Install firmware",
        ProgressNextStep.ReinstallFirmware => "Reinstall firmware…",
        ProgressNextStep.ChooseHowToStart => "Choose how to start",
        _ => null,
    };

    public bool HasNext => NextStep != ProgressNextStep.None;

    private Action? _next;
    private Func<bool>? _canNext;

    private void OnControllerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.PropertyName == nameof(ControllerViewModel.NeedsFirmwarePort)) OnPortQuestion();
        Update();
        NextCommand.NotifyCanExecuteChanged();
    }

    private void OnPortQuestion()
    {
        if (_controller.NeedsFirmwarePort)
        {
            _generation++;
            _operation = Operation.None;
            ResultText = null;
            SetNext(ProgressNextStep.None, null, null);
            Title = _controller.FirmwareQuestionTitle;
            OnPropertyChanged(nameof(PortChoices));
            IsRunning = false;
            IsAskingPort = true;
            IsOpen = true;
        }
        else if (IsAskingPort)
        {
            IsAskingPort = false;   // answered (the run opens the dialog again) or given up
            if (!_controller.IsFirmwareRunning && !_controller.IsSaving) Hide();
        }
    }

    private Operation Running()
    {
        if (_controller.IsFirmwareRunning)
            return _controller.FirmwareActivity switch
            {
                LinkActivity.InstallingFirmware => Operation.Install,
                LinkActivity.UpdatingFirmware => Operation.Update,
                _ => Operation.Check,
            };
        return _controller.IsSaving ? Operation.Save : Operation.None;
    }

    /// <summary>An operation that starts opens the dialog (or takes over the one showing the last
    /// result); while it runs its step and progress follow the page.</summary>
    private void Update()
    {
        var now = Running();
        if (now == Operation.None) return;   // the end is the operation's finished event
        IsAskingPort = false;   // an operation that runs took the dialog over
        if (!IsRunning || now != _operation)
        {
            _generation++;
            _operation = now;
            ResultText = null;
            ResultTone = LinkTone.Neutral;
            SetNext(ProgressNextStep.None, null, null);
            IsRunning = true;
            IsOpen = true;
        }
        Title = now switch
        {
            Operation.Install => "Installing firmware",
            Operation.Update => "Updating firmware",
            Operation.Check => "Checking firmware",
            _ => "Saving to the controller",
        };
        if (now == Operation.Save)
        {
            Step = _controller.SavingPhase == SavePhase.Confirming ? "Restarting…" : "Sending…";
            Progress = null;
        }
        else
        {
            Step = now == Operation.Check ? "Reading the board's firmware…" : _controller.FirmwareStep switch
            {
                FlashStage.Erasing => "Erasing…",
                FlashStage.Writing => $"Writing the firmware… {Math.Round(_controller.FirmwareProgress * 100):0}%",
                FlashStage.Verifying => "Verifying…",
                FlashStage.Restarting => "Restarting…",
                _ => "Connecting to the board…",
            };
            // A check has no measure, nor a write before it writes (the bar runs without one); a
            // write's bar fills while it writes and stays full after.
            Progress = now != Operation.Check && _controller.FirmwareStep >= FlashStage.Writing
                ? _controller.FirmwareProgress : null;
        }
    }

    private void OnFirmwareFinished(object? sender, EventArgs e)
    {
        if (_disposed || !IsRunning || _operation is Operation.Save or Operation.None) return;
        var c = _controller;
        IsRunning = false;
        Step = null;
        ResultText = c.FirmwareMessage;
        ResultTone = c.FirmwareMessageTone;
        var operation = _operation;
        var activity = operation switch
        {
            Operation.Install => LinkActivity.InstallingFirmware,
            Operation.Update => LinkActivity.UpdatingFirmware,
            _ => LinkActivity.CheckingFirmware,
        };
        if (c.OfferTest) OfferTest();
        else if (c.OfferInstall)   // a check offers an install: it asks first, as from the banner
            SetNext(c.OfferFreshInstall ? ProgressNextStep.InstallFirmware : ProgressNextStep.ReinstallFirmware,
                    () => c.InstallFirmwareCommand.Execute(null), () => c.CanUseFirmware);
        else if (operation == Operation.Install && c.FirmwareMessageTone == LinkTone.Good && c.IsNotSetUp)
            // The install left the controller without a setup: the chooser, on the Controller page.
            SetNext(ProgressNextStep.ChooseHowToStart, _toController, null);
        else if (c.FirmwareMessageTone == LinkTone.Danger)
            SetNext(ProgressNextStep.TryAgain, () => c.RetryFirmware(activity), () => c.CanUseFirmware);
    }

    private void OnSaveFinished(object? sender, SaveStatus status)
    {
        if (_disposed || !IsRunning || _operation != Operation.Save) return;
        var c = _controller;
        IsRunning = false;
        Step = null;
        if (status == SaveStatus.Confirmed)
        {
            ResultTone = LinkTone.Good;
            if (c.OfferTest)
            {
                ResultText = "Saved. The controller is set up.";
                OfferTest();
                return;
            }
            ResultText = "Saved.";
            CloseSoon();
            return;
        }
        ResultText = c.ProgressText;
        ResultTone = LinkTone.Danger;
        if (c.LastSaveCanRetry) SetNext(ProgressNextStep.TryAgain, c.RetrySave, () => c.CanRetrySave);
    }

    /// <summary>A plain success needs nothing more: the dialog goes by itself after a moment.</summary>
    private void CloseSoon()
    {
        if (SavedHold <= TimeSpan.Zero)
        {
            Close();
            return;
        }
        var generation = _generation;
        _ = Task.Delay(SavedHold).ContinueWith(_ => _controller.Ui.Post(() =>
        {
            if (!_disposed && generation == _generation && IsDone) Close();
        }), TaskScheduler.Default);
    }

    // ── The Connection dialog's Wi-Fi operations (hardware-page spec §10.2, §10.3) ─────────────

    /// <summary>A Wi-Fi save starts (Save and switch, Save over Wi-Fi, or turning Wi-Fi off once the
    /// controller answers over USB): the dialog opens with <paramref name="title"/> and
    /// <paramref name="step"/>, and nothing closes it until <see cref="EndWifi"/>.</summary>
    internal void BeginWifi(string title, string step)
    {
        if (_disposed) return;
        _generation++;
        _operation = Operation.Wifi;
        ResultText = null;
        ResultTone = LinkTone.Neutral;
        SetNext(ProgressNextStep.None, null, null);
        Title = title;
        Step = step;
        Progress = null;
        IsRunning = true;
        IsOpen = true;
    }

    /// <summary>The running Wi-Fi save's step ("Saved to the controller · restarting", "Waiting for
    /// the controller over Wi-Fi… 9 s").</summary>
    internal void WifiStep(string step)
    {
        if (!_disposed && _operation == Operation.Wifi && IsRunning) Step = step;
    }

    /// <summary>The Wi-Fi save ended with <paramref name="result"/>: a good one shows briefly, as a
    /// plain save's "Saved." does; a danger one stays until Close (a turn-off whose result has no
    /// other place); null closes the dialog at once (the Connection dialog's card says what went
    /// wrong).</summary>
    internal void EndWifi(string? result, LinkTone tone = LinkTone.Good)
    {
        if (_disposed || _operation != Operation.Wifi || !IsRunning) return;
        IsRunning = false;
        Step = null;
        if (result is null)
        {
            Hide();
            return;
        }
        ResultTone = tone;
        ResultText = result;
        if (tone == LinkTone.Good) CloseSoon();
    }

    /// <summary>A Wi-Fi operation that ended before it showed anything failed: its result shows
    /// under <paramref name="title"/> until Close.</summary>
    internal void ShowWifiFailure(string title, string result)
    {
        BeginWifi(title, "");
        EndWifi(result, LinkTone.Danger);
    }

    private void OfferTest()
        => SetNext(ProgressNextStep.TestNow, () =>
        {
            _toController();
            _controller.TestNowCommand.Execute(null);
        }, () => _controller.CanStartTest);

    private void SetNext(ProgressNextStep step, Action? next, Func<bool>? canNext)
    {
        _next = next;
        _canNext = canNext;
        NextStep = step;
        NextCommand.NotifyCanExecuteChanged();
    }

    private bool CanNext => IsDone && _next is not null && (_canNext?.Invoke() ?? true);

    /// <summary>The next step: it closes the dialog first (Try again opens it again).</summary>
    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next()
    {
        if (!CanNext) return;
        var next = _next!;
        Hide();
        next();
    }

    private bool CanClose => !IsRunning;   // asking can be abandoned

    /// <summary>Close (also Escape and the scrim once the operation ended): what still needs the
    /// user shows in the banner; an offered test is declined.</summary>
    [RelayCommand(CanExecute = nameof(CanClose))]
    public void Close()
    {
        if (IsRunning || !IsOpen) return;
        if (IsAskingPort)
        {
            _controller.CancelFirmwarePort();   // its change event hides the dialog
            return;
        }
        if (NextStep == ProgressNextStep.TestNow) _controller.DeclineTest();
        Hide();
    }

    private void Hide()
    {
        _generation++;
        SetNext(ProgressNextStep.None, null, null);
        ResultText = null;
        _operation = Operation.None;
        IsOpen = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _controller.PropertyChanged -= OnControllerChanged;
        _controller.FirmwareFinished -= OnFirmwareFinished;
        _controller.SaveFinished -= OnSaveFinished;
    }
}
