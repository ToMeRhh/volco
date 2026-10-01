using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Volco.App.ViewModels;

/// <summary>One preset tab on the Buttons page.</summary>
public sealed partial class PresetTabViewModel : ObservableObject
{
    private readonly Action<PresetTabViewModel> _select;

    public string Id { get; }

    [ObservableProperty]
    private string _name;

    /// <summary>The preset whose actions the cards are showing.</summary>
    [ObservableProperty]
    private bool _isEditing;

    /// <summary>The preset the controller is using right now.</summary>
    [ObservableProperty]
    private bool _isLive;

    internal PresetTabViewModel(string id, string name, Action<PresetTabViewModel> select)
    {
        Id = id;
        _name = name;
        _select = select;
    }

    [RelayCommand]
    private void Select() => _select(this);
}
