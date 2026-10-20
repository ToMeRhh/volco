using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Volco.App.ViewModels.Hardware;

/// <summary>
/// The Hardware inspector of a slider (hardware-page spec §6.1): "Slider N", what it controls
/// (from Controls, read-only here), its pin and Remove. Made for one draft slider; the Hardware
/// view model refreshes it after every draft change and disposes it when the selection moves.
/// </summary>
public sealed partial class SliderWiringViewModel : ObservableObject, IDisposable
{
    private readonly SliderItemViewModel _item;

    internal SliderWiringViewModel(int index, SliderItemViewModel item, PinFieldViewModel pin, Action remove)
    {
        Index = index;
        _item = item;
        Pin = pin;
        RemoveCommand = new RelayCommand(remove);
        _item.PropertyChanged += OnItemPropertyChanged;
    }

    /// <summary>The draft slider (0-based).</summary>
    public int Index { get; }

    /// <summary>The Controls item it draws.</summary>
    public SliderItemViewModel Item => _item;

    /// <summary>"Slider N".</summary>
    public string Title => $"Slider {Index + 1}";

    /// <summary>"Controls: Speakers".</summary>
    public string Subtitle => $"Controls: {_item.TargetLabel}";

    public PinFieldViewModel Pin { get; }

    /// <summary>"Remove Slider N".</summary>
    public string RemoveText => $"Remove {Title}";

    /// <summary>Asks "Remove Slider N?", then removes it from the draft.</summary>
    public IRelayCommand RemoveCommand { get; }

    /// <summary>The draft can be edited: Remove is enabled.</summary>
    [ObservableProperty] private bool _isEditable;

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SliderItemViewModel.TargetLabel) or null or "") OnPropertyChanged(nameof(Subtitle));
    }

    public void Dispose() => _item.PropertyChanged -= OnItemPropertyChanged;
}
