using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Volco.App.ViewModels;

/// <summary>One mode tab on the Controls page.</summary>
public sealed partial class PresetTabViewModel : ObservableObject
{
    private readonly Action<PresetTabViewModel> _select;

    public string Id { get; }

    /// <summary>The preset's colour: its position in the list (<see cref="Controls.PresetTint"/>).</summary>
    public int TintIndex { get; }

    [ObservableProperty]
    private string _name;

    /// <summary>The preset whose actions the buttons are showing.</summary>
    [ObservableProperty]
    private bool _isEditing;

    /// <summary>The preset the controller is using right now.</summary>
    [ObservableProperty]
    private bool _isLive;

    internal PresetTabViewModel(string id, string name, int tintIndex, Action<PresetTabViewModel> select)
    {
        Id = id;
        TintIndex = tintIndex;
        _name = name;
        _select = select;
    }

    [RelayCommand]
    private void Select() => _select(this);
}
