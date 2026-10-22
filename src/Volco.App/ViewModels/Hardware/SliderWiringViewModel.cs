using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Volco.App.ViewModels.Hardware;

/// <summary>
/// The Hardware section of a slider's inspector (controller-page spec §4.3, hardware-page spec
/// §6.1): its pin and Remove, and the danger mark on the section's header. Made for one draft
/// slider; the Hardware view model refreshes it after every draft change and drops it when the selection moves.
/// </summary>
public sealed partial class SliderWiringViewModel : ObservableObject
{
    internal SliderWiringViewModel(int index, SliderItemViewModel item, PinFieldViewModel pin, Action remove)
    {
        Index = index;
        Item = item;
        Pin = pin;
        RemoveCommand = new RelayCommand(remove);
    }

    /// <summary>The draft slider (0-based).</summary>
    public int Index { get; }

    /// <summary>The Controls item it draws.</summary>
    public SliderItemViewModel Item { get; }

    /// <summary>"Slider N".</summary>
    public string Title => $"Slider {Index + 1}";

    public PinFieldViewModel Pin { get; }

    /// <summary>"Remove Slider N".</summary>
    public string RemoveText => $"Remove {Title}";

    /// <summary>Asks "Remove Slider N?", then removes it from the draft.</summary>
    public IRelayCommand RemoveCommand { get; }

    /// <summary>The draft can be edited: Remove is enabled.</summary>
    [ObservableProperty] private bool _isEditable;

    /// <summary>The draft has a problem with this slider: the section's danger mark.</summary>
    [ObservableProperty] private bool _hasProblem;
}
