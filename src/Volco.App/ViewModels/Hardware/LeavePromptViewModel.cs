using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Controller;

namespace Volco.App.ViewModels.Hardware;

/// <summary>
/// Leaving Hardware with unsaved changes asks first (hardware-page spec §3.3): Save and continue
/// (waits for the controller's confirmation, with its progress here; a failure keeps the user on
/// Hardware with the error on the card), Discard and continue, or Stay. Save is disabled while the
/// draft has problems. Raised by the settings window for any page; hiding the window doesn't ask.
/// </summary>
public sealed partial class LeavePromptViewModel : ObservableObject, IDisposable
{
    private readonly ControllerViewModel _controller;
    private readonly Action _proceed;
    private readonly Action _close;
    private bool _disposed;

    /// <param name="proceed">Goes where the user was going; runs after the prompt closed.</param>
    /// <param name="close">Closes the prompt (the user stays on Hardware).</param>
    public LeavePromptViewModel(ControllerViewModel controller, Action proceed, Action close)
    {
        _controller = controller;
        _proceed = proceed;
        _close = close;
        SaveAndContinueCommand = new RelayCommand(SaveAndContinue, () => CanSaveAndContinue);
        DiscardAndContinueCommand = new RelayCommand(DiscardAndContinue, () => !IsSaving);
        StayCommand = new RelayCommand(Stay);
        _controller.PropertyChanged += OnControllerChanged;
        _controller.SaveFinished += OnSaveFinished;
    }

    public string Title => "Unsaved changes";

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
