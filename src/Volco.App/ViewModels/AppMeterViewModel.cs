using CommunityToolkit.Mvvm.ComponentModel;

namespace Volco.App.ViewModels;

/// <summary>The live level (0 to 1) of one app's audio in the target dialog; the dialog's timer
/// sets it.</summary>
public sealed partial class AppMeterViewModel : ObservableObject
{
    [ObservableProperty]
    private double _level;
}
