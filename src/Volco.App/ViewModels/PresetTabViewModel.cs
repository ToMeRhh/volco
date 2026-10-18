using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Volco.App.ViewModels;

/// <summary>One mode tab on the Controls page.</summary>
public sealed partial class PresetTabViewModel : ObservableObject
{
    private readonly Action<PresetTabViewModel> _select;
    private readonly Action<string> _delete;

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

    /// <summary>Being renamed inline.</summary>
    [ObservableProperty]
    private bool _isRenaming;

    /// <summary>The first mode, which the controller starts in.</summary>
    public bool IsFirst => TintIndex == 0;

    /// <summary>The first mode can't be deleted.</summary>
    public bool CanDelete => !IsFirst;

    internal PresetTabViewModel(string id, string name, int tintIndex, Action<PresetTabViewModel> select,
                                Action<string> delete)
    {
        _delete = delete;
        Id = id;
        TintIndex = tintIndex;
        _name = name;
        _select = select;
    }

    [RelayCommand]
    private void Select() => _select(this);

    [RelayCommand]
    private void Delete() => _delete(Id);
}
