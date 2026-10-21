using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Controller;
using Volco.Core.Firmware;
using Volco.Core.Runtime;

namespace Volco.App.ViewModels.Hardware;

/// <summary>
/// The status's third level (controller-page spec §5, "Running, don't interrupt"): a modal dialog
/// over the window while a firmware install, update or check, or a save to the controller, runs.
/// It shows the operation's name, its step and progress and "Don't unplug the controller.", and
/// can't be closed while it writes. At the end it shows the result and its next step (Test your
/// controller now after an update or a first save, Try again after a failure) and stays until
/// Close; a plain confirmed save shows "Saved." briefly and closes itself. Closing leaves what
/// still needs the user to the banner. UI thread.
/// </summary>
public sealed partial class ProgressDialogViewModel : ObservableObject, IDisposable
{
    public const string WarningText = "Don't unplug the controller.";

    /// <summary>How long a confirmed save's "Saved." stays before the dialog closes itself.</summary>
    internal static TimeSpan SavedHold { get; set; } = TimeSpan.FromSeconds(1);

    private enum Operation { None, Install, Update, Check, Save }

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
    public bool IsDone => IsOpen && !IsRunning;

    partial void OnIsOpenChanged(bool value) => OnPropertyChanged(nameof(IsDone));

    public bool HasMeasure => Progress is not null;

    [ObservableProperty] private string? _resultText;

    [ObservableProperty] private LinkTone _resultTone;

    /// <summary>The next step's button: "Test your controller now", "Try again", or null for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNext))]
    private string? _nextLabel;

    public bool HasNext => NextLabel is not null;

    private Action? _next;
    private Func<bool>? _canNext;

    private void OnControllerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        Update();
        NextCommand.NotifyCanExecuteChanged();
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
        if (!IsRunning || now != _operation)
        {
            _generation++;
            _operation = now;
            ResultText = null;
            ResultTone = LinkTone.Neutral;
            SetNext(null, null, null);
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
            // A check has no measure; a write's bar fills while it writes and stays full after.
            Progress = now == Operation.Check ? null
                     : _controller.FirmwareStep >= FlashStage.Writing ? _controller.FirmwareProgress : 0;
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
        else if (c.FirmwareMessageTone == LinkTone.Danger)
            SetNext("Try again", () => c.RetryFirmware(activity), () => c.CanUseFirmware);
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
            // A plain save needs nothing more: the dialog goes by itself after a moment.
            if (SavedHold <= TimeSpan.Zero)
            {
                Close();
                return;
            }
            var generation = _generation;
            _ = Task.Delay(SavedHold).ContinueWith(_ => c.Ui.Post(() =>
            {
                if (!_disposed && generation == _generation && IsDone) Close();
            }), TaskScheduler.Default);
            return;
        }
        ResultText = c.ProgressText;
        ResultTone = LinkTone.Danger;
        if (c.LastSaveCanRetry) SetNext("Try again", c.RetrySave, () => c.CanRetrySave);
    }

    private void OfferTest()
        => SetNext("Test your controller now", () =>
        {
            _toController();
            _controller.TestNowCommand.Execute(null);
        }, () => _controller.CanStartTest);

    private void SetNext(string? label, Action? next, Func<bool>? canNext)
    {
        _next = next;
        _canNext = canNext;
        NextLabel = label;
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

    private bool CanClose => !IsRunning;

    /// <summary>Close (also Escape and the scrim once the operation ended): what still needs the
    /// user shows in the banner; an offered test is declined.</summary>
    [RelayCommand(CanExecute = nameof(CanClose))]
    public void Close()
    {
        if (IsRunning || !IsOpen) return;
        if (NextLabel == "Test your controller now") _controller.DeclineTest();
        Hide();
    }

    private void Hide()
    {
        _generation++;
        SetNext(null, null, null);
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
