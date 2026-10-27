using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Controller;

namespace Volco.App.ViewModels.Hardware;

/// <summary>Where the user was going when the leave prompt asked: its words follow.</summary>
public enum LeaveKind
{
    /// <summary>Another page ("Save and continue", "Discard and continue", "Stay").</summary>
    Leave,
    /// <summary>The window closes to the tray ("Save and close", "Discard and close", "Cancel").</summary>
    Close,
    /// <summary>The Connection dialog opens (hardware-page spec §10), with the Leave buttons.</summary>
    Connection,
}

/// <summary>
/// Leaving Hardware with unsaved changes asks first (hardware-page spec §3.3): Save and continue
/// (waits for the controller's confirmation, with its progress here; a failure keeps the user on
/// Hardware with the error on the card), Discard and continue, or Stay. Save is disabled while the
/// draft has problems. Raised by the settings window for any page, by its close button (the
/// window hides to the tray), when the buttons say "close" and Stay is Cancel, and before the
/// Connection dialog opens (<see cref="LeaveKind"/>).
/// A save already running (the save bar's) is adopted: the prompt waits for it as for its own.
/// </summary>
public sealed partial class LeavePromptViewModel : ObservableObject, IDisposable
{
    private readonly ControllerViewModel _controller;
    private Action _proceed;
    private LeaveKind _kind;
    private readonly Action _close;
    private bool _disposed;

    /// <param name="proceed">Goes where the user was going; runs after the prompt closed.</param>
    /// <param name="close">Closes the prompt (the user stays on Hardware).</param>
    /// <param name="kind">Where the user was going: another page, closing the window, or the Connection dialog.</param>
    public LeavePromptViewModel(ControllerViewModel controller, Action proceed, Action close, LeaveKind kind = LeaveKind.Leave)
    {
        _controller = controller;
        _proceed = proceed;
        _kind = kind;
        _close = close;
        _isSaving = controller.IsSaving;
        SaveAndContinueCommand = new RelayCommand(SaveAndContinue, () => CanSaveAndContinue);
        DiscardAndContinueCommand = new RelayCommand(DiscardAndContinue, () => !IsSaving);
        StayCommand = new RelayCommand(Stay);
        _controller.PropertyChanged += OnControllerChanged;
        _controller.SaveFinished += OnSaveFinished;
    }

    public string Title => "Unsaved changes";

    public string Message => _kind switch
    {
        LeaveKind.Close => "Your controller changes aren't saved. Save them before closing?",
        LeaveKind.Connection => "Your controller changes aren't saved. Save them before opening Connection?",
        _ => "Your controller changes aren't saved. Save them before leaving?",
    };

    private bool Closing => _kind == LeaveKind.Close;

    public string SaveLabel => Closing ? "Save and close" : "Save and continue";
    public string DiscardLabel => Closing ? "Discard and close" : "Discard and continue";
    public string StayLabel => Closing ? "Cancel" : "Stay";

    /// <summary>Another page was chosen, the window closed, or Connection was opened while the
    /// prompt is open: the prompt (and a save it runs) stays, and continuing goes there instead.</summary>
    public void Retarget(Action proceed, LeaveKind kind = LeaveKind.Leave)
    {
        _proceed = proceed;
        if (_kind == kind) return;
        _kind = kind;
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(SaveLabel));
        OnPropertyChanged(nameof(DiscardLabel));
        OnPropertyChanged(nameof(StayLabel));
    }

    /// <summary>Save and continue's save runs: the prompt waits for the controller's confirmation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveAndContinue), nameof(ProgressText))]
    private bool _isSaving;

    /// <summary>The save's progress ("Saving to the controller…", "Restarting…") while it runs.</summary>
    public string? ProgressText => IsSaving ? _controller.SavePhaseText ?? "Saving to the controller…" : null;

    /// <summary>Save is offered: the draft can be saved and has no problems.</summary>
    public bool CanSaveAndContinue => !IsSaving && _controller.CanSave && _controller.Problems.Count == 0;

    public IRelayCommand SaveAndContinueCommand { get; }
    public IRelayCommand DiscardAndContinueCommand { get; }
    public IRelayCommand StayCommand { get; }

    private void SaveAndContinue()
    {
        if (!CanSaveAndContinue) return;
        IsSaving = true;
        RaiseCommands();
        _controller.SaveCommand.Execute(null);
        // The save didn't start: a question (the Wi-Fi warning) or a reason now shows on Hardware.
        if (!_controller.IsSaving && IsSaving) Stay();
    }

    private void OnSaveFinished(object? sender, SaveStatus status)
    {
        if (_disposed || !IsSaving) return;
        IsSaving = false;
        _close();
        if (status == SaveStatus.Confirmed) _proceed();   // otherwise the card shows the error
    }

    private void DiscardAndContinue()
    {
        if (IsSaving) return;
        _controller.RevertCommand.Execute(null);
        _close();
        _proceed();
    }

    private void Stay() => _close();

    private void OnControllerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        OnPropertyChanged(nameof(CanSaveAndContinue));
        OnPropertyChanged(nameof(ProgressText));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        SaveAndContinueCommand.NotifyCanExecuteChanged();
        DiscardAndContinueCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _controller.PropertyChanged -= OnControllerChanged;
        _controller.SaveFinished -= OnSaveFinished;
    }
}
