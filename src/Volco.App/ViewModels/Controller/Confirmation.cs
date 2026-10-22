using CommunityToolkit.Mvvm.Input;

namespace Volco.App.ViewModels;

/// <summary>A question the Controller page asks inline, in its side panel (the app has no dialog
/// windows). Confirm runs the action; either button closes it.</summary>
public sealed class Confirmation
{
    public Confirmation(string message, string confirmLabel, Action onConfirm, Action close, bool isDestructive = false)
    {
        Message = message;
        IsDestructive = isDestructive;
        ConfirmLabel = confirmLabel;
        ConfirmCommand = new RelayCommand(() => { close(); onConfirm(); });
        CancelCommand = new RelayCommand(close);
    }

    public string Message { get; }
    public string ConfirmLabel { get; }

    /// <summary>Confirm loses something (Remove): its button is red. Other questions (the Wi-Fi
    /// warning before a save, Load a preset) use the primary style.</summary>
    public bool IsDestructive { get; }
    public IRelayCommand ConfirmCommand { get; }
    public IRelayCommand CancelCommand { get; }
}
