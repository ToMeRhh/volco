using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Volco.App.ViewModels;

/// <summary>
/// The one "… removed · Undo" message the settings window shows after a destructive change
/// (Reset, Remove last, Delete mode). Showing a new one replaces the old one, whose undo is
/// then no longer offered. The window closes it after a timeout (<see cref="Version"/> tells it
/// which message the timeout belongs to).
/// </summary>
public sealed partial class UndoToastViewModel : ObservableObject
{
    private Action? _undo;

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private bool _isOpen;

    /// <summary>Increments with every <see cref="Show"/>.</summary>
    [ObservableProperty]
    private int _version;

    public void Show(string message, Action undo)
    {
        _undo = undo;
        Message = message;
        IsOpen = true;
        Version++;
    }

    [RelayCommand]
    private void Undo()
    {
        var undo = _undo;
        Dismiss();
        undo?.Invoke();
    }

    [RelayCommand]
    public void Dismiss()
    {
        _undo = null;
        IsOpen = false;
    }
}
