using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Volco.App.ViewModels;

/// <summary>One mode tab on the Controller page.</summary>
public sealed partial class ModeTabViewModel : ObservableObject
{
    private readonly Action<ModeTabViewModel> _select;
    private readonly Action<string> _delete;

    public string Id { get; }

    /// <summary>The mode's colour: its position in the list (<see cref="Controls.ModeTint"/>).</summary>
    public int TintIndex { get; }

    [ObservableProperty]
    private string _name;

    /// <summary>The mode whose actions the buttons are showing.</summary>
    [ObservableProperty]
    private bool _isEditing;

    /// <summary>The mode the controller is using right now.</summary>
    [ObservableProperty]
    private bool _isLive;

    /// <summary>Being renamed inline.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowName))]
    private bool _isRenaming;

    /// <summary>The strip is in edit mode (the pencil): the "start" label and × show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStartLabel), nameof(ShowDelete))]
    private bool _isEditingModes;

    public bool ShowName => !IsRenaming;
    public bool ShowStartLabel => IsEditingModes && IsFirst;
    public bool ShowDelete => IsEditingModes && CanDelete;

    /// <summary>The first mode, which the controller starts in.</summary>
    public bool IsFirst => TintIndex == 0;

    /// <summary>The first mode can't be deleted.</summary>
    public bool CanDelete => !IsFirst;

    internal ModeTabViewModel(string id, string name, int tintIndex, Action<ModeTabViewModel> select,
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

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete() => _delete(Id);
}
