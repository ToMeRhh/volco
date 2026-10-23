using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;

namespace Volco.App.ViewModels.Hardware;

/// <summary>
/// The I2C buses dialog (controller-page spec §4.3), opened from Controller settings › Screens and
/// from a screen button's bus link: one card per bus the draft has or a screen uses, in bus order,
/// with its pins, its clock and Remove; Add bus adds the lowest free number (0-3). Edits go to the
/// draft at once, like the rest of the Hardware section, so Done only closes it.
/// </summary>
public sealed partial class I2cBusesViewModel : ObservableObject
{
    private readonly ControllerViewModel _controller;
    private readonly Func<PinOwner, PinFieldViewModel> _pin;
    private readonly Func<int, int, CancellationToken, Task<IReadOnlyList<int>?>>? _scan;
    private IReadOnlyList<BusWiringViewModel> _buses = [];

    /// <param name="pin">The pin field of a use (a bus's SDA or SCL), shared with the rest of the page.</param>
    /// <param name="focus">The bus the dialog was opened for (its card is marked), or null.</param>
    /// <param name="scan">The I2C scan of a bus's pins (Find screens); null for none.</param>
    internal I2cBusesViewModel(ControllerViewModel controller, Func<PinOwner, PinFieldViewModel> pin, Action close, int? focus,
                               Func<int, int, CancellationToken, Task<IReadOnlyList<int>?>>? scan = null)
    {
        _controller = controller;
        _pin = pin;
        _scan = scan;
        FocusedBus = focus;
        DoneCommand = new RelayCommand(close);
        AddCommand = new RelayCommand(Add, () => CanAdd);
        Refresh();
    }

    public string Title => "I2C buses";

    /// <summary>The bus the dialog was opened for, or null.</summary>
    public int? FocusedBus { get; }

    /// <summary>The buses the draft has or a screen uses, in bus order.</summary>
    public IReadOnlyList<BusWiringViewModel> Buses => _buses;

    /// <summary>No bus yet: the dialog says how to add one.</summary>
    public bool HasNoBuses => _buses.Count == 0;

    /// <summary>A bus number is free and the draft can be edited.</summary>
    [ObservableProperty] private bool _canAdd;

    /// <summary>"Add bus 1": the lowest free number.</summary>
    [ObservableProperty] private string _addText = "Add bus";

    public IRelayCommand AddCommand { get; }

    /// <summary>Closes the dialog; the edits are already in the draft.</summary>
    public IRelayCommand DoneCommand { get; }

    /// <summary>The buses the draft has or a screen uses (0-3), ascending.</summary>
    internal static IReadOnlyList<int> Listed(ControllerConfig draft)
        => Enumerable.Range(0, 4).Where(b => ControllerDraft.Bus(draft, b) is not null)
                     .Union(ControllerDraft.UsedBuses(draft)).Order().ToList();

    /// <summary>The draft changed: the list follows it and every card re-reads its values.</summary>
    internal void Refresh()
    {
        var listed = Listed(_controller.Draft);
        if (!_buses.Select(b => b.Bus).SequenceEqual(listed))
        {
            var old = _buses.ToDictionary(b => b.Bus);
            _buses = listed.Select(bus => old.TryGetValue(bus, out var row) ? row
                : new BusWiringViewModel(bus, _controller, _pin(new PinOwner(PinOwnerKind.BusSda, bus)),
                                         _pin(new PinOwner(PinOwnerKind.BusScl, bus)), Remove, _scan) { IsFocused = bus == FocusedBus }).ToList();
            OnPropertyChanged(nameof(Buses));
            OnPropertyChanged(nameof(HasNoBuses));
        }
        foreach (var row in _buses) row.Refresh();
        var free = Enumerable.Range(0, 4).Except(listed).Cast<int?>().FirstOrDefault();
        CanAdd = free is not null && _controller.CanEdit;
        AddText = free is { } n ? $"Add bus {n}" : "Add bus";
        AddCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Adds the lowest free bus, its pins not set yet (400 kHz).</summary>
    private void Add()
    {
        if (!_controller.CanEdit) return;
        var free = Enumerable.Range(0, 4).Except(Listed(_controller.Draft)).Cast<int?>().FirstOrDefault();
        if (free is not { } bus) return;
        ControllerDraft.SetBus(_controller.Draft, bus, new I2cBus());
        _controller.OnDraftEdited();
        Refresh();
    }

    /// <summary>Takes a bus no screen uses out of the draft.</summary>
    private void Remove(int bus)
    {
        if (!_controller.CanEdit || BusWiringViewModel.ScreensOn(_controller.Draft, bus).Count > 0) return;
        ControllerDraft.SetBus(_controller.Draft, bus, null);
        _controller.OnDraftEdited();
        Refresh();
    }
}
