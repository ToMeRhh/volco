using CommunityToolkit.Mvvm.Input;

namespace Volco.App.ViewModels;

/// <summary>A question the Controller page asks inline, above its footer (the app has no dialog
/// windows). Confirm runs the action; either button closes it.</summary>
public sealed class Confirmation
{
    public Confirmation(string message, string confirmLabel, Action onConfirm, Action close)
    {
        Message = message;
        ConfirmLabel = confirmLabel;
        ConfirmCommand = new RelayCommand(() => { close(); onConfirm(); });
        CancelCommand = new RelayCommand(close);
    }

    public string Message { get; }
    public string ConfirmLabel { get; }
    public IRelayCommand ConfirmCommand { get; }
    public IRelayCommand CancelCommand { get; }
}
