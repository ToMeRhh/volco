using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;

namespace Volco.App.ViewModels.Hardware;

/// <summary>One chip under the buttons for a bus that a screen uses (hardware-page spec §5.2):
/// "I2C bus 0 · SDA 25 · SCL 26"; a click selects the bus.</summary>
public sealed partial class BusChipViewModel : ObservableObject
{
    internal BusChipViewModel(int bus, Action<int> select)
    {
        Bus = bus;
        SelectCommand = new RelayCommand(() => select(Bus));
    }

    /// <summary>The bus's hardware number (0-3).</summary>
    public int Bus { get; }

    [ObservableProperty] private string _text = "";

    /// <summary>The inspector shows this bus: the accent outline.</summary>
    [ObservableProperty] private bool _isSelected;

    public IRelayCommand SelectCommand { get; }
}

/// <summary>
/// The Hardware inspector of an I2C bus (hardware-page spec §6.4): its SDA and SCL pins and its
/// clock (100 or 400 kHz). A clock set elsewhere to another value shows no choice and is kept.
/// </summary>
public sealed partial class BusWiringViewModel : ObservableObject
{
    private const uint Slow = 100_000;
    private const uint Fast = 400_000;

    private readonly ControllerViewModel _controller;

    internal BusWiringViewModel(int bus, ControllerViewModel controller, PinFieldViewModel sda, PinFieldViewModel scl)
    {
        Bus = bus;
        _controller = controller;
        Sda = sda;
        Scl = scl;
    }

    /// <summary>The bus's hardware number (0-3).</summary>
    public int Bus { get; }

    /// <summary>"I2C bus N".</summary>
    public string Title => $"I2C bus {Bus}";

    public PinFieldViewModel Sda { get; }
    public PinFieldViewModel Scl { get; }

    public static IReadOnlyList<string> ClockChoices { get; } = new[] { "100 kHz", "400 kHz" };

    /// <summary>The draft can be edited: the clock is enabled.</summary>
    [ObservableProperty] private bool _isEditable;

    private I2cBus? Message => ControllerDraft.Bus(_controller.Draft, Bus);

    /// <summary>0 = 100 kHz, 1 = 400 kHz (the default), -1 for another clock.</summary>
    public int ClockIndex
    {
        get => (Message?.ClockHz ?? Fast) switch { Slow => 0, Fast => 1, _ => -1 };
        set
        {
            if (value is not (0 or 1) || value == ClockIndex || !_controller.CanEdit) { OnPropertyChanged(); return; }
            var bus = Message;
            if (bus is null) ControllerDraft.SetBus(_controller.Draft, Bus, bus = new I2cBus());
            bus.ClockHz = value == 0 ? Slow : Fast;
            OnPropertyChanged();
            _controller.OnDraftEdited();
        }
    }

    /// <summary>The draft changed: re-reads every value.</summary>
    internal void Refresh()
    {
        IsEditable = _controller.CanEdit;
        OnPropertyChanged(nameof(ClockIndex));
    }
}
