using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;

namespace Volco.App.ViewModels.Hardware;

/// <summary>
/// One I2C bus in the I2C buses dialog (controller-page spec §4.3): "Bus 0 · Hardware I2C", which
/// screens use it, its SDA and SCL pins and its clock (100 or 400 kHz), and Remove, which only a bus
/// no screen uses has. A clock set elsewhere to another value shows no choice and is kept.
/// </summary>
public sealed partial class BusWiringViewModel : ObservableObject
{
    private const uint Slow = 100_000;
    private const uint Fast = 400_000;

    private readonly ControllerViewModel _controller;

    internal BusWiringViewModel(int bus, ControllerViewModel controller, PinFieldViewModel sda, PinFieldViewModel scl, Action<int> remove)
    {
        Bus = bus;
        _controller = controller;
        Sda = sda;
        Scl = scl;
        RemoveCommand = new RelayCommand(() => remove(Bus), () => CanRemove);
        Refresh();
    }

    /// <summary>The bus's hardware number (0-3).</summary>
    public int Bus { get; }

    /// <summary>"Bus N".</summary>
    public string Title => $"Bus {Bus}";

    /// <summary>Buses 0 and 1 are the ESP32's I2C hardware; 2 and 3 run in software.</summary>
    public string KindText => Bus < 2 ? "Hardware I2C" : "Software I2C";

    /// <summary>"Used by Button 1 and Button 3", or "No screen uses it".</summary>
    [ObservableProperty] private string _usedByText = "";

    /// <summary>The draft has a problem with this bus: the card's danger mark.</summary>
    [ObservableProperty] private bool _hasProblem;

    /// <summary>The dialog was opened for this bus (a button's bus link): its card is marked.</summary>
    [ObservableProperty] private bool _isFocused;

    public PinFieldViewModel Sda { get; }
    public PinFieldViewModel Scl { get; }

    public static IReadOnlyList<string> ClockChoices { get; } = new[] { "100 kHz", "400 kHz" };

    /// <summary>The draft can be edited: the clock is enabled.</summary>
    [ObservableProperty] private bool _isEditable;

    /// <summary>No screen uses the bus and the draft can be edited.</summary>
    [ObservableProperty] private bool _canRemove;

    /// <summary>Why Remove is disabled while screens use the bus, else null.</summary>
    [ObservableProperty] private string? _removeTooltip;

    /// <summary>Takes the bus out of the draft (its pins become free).</summary>
    public IRelayCommand RemoveCommand { get; }

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
        var users = ScreensOn(_controller.Draft, Bus);
        UsedByText = users.Count == 0 ? "No screen uses it" : $"Used by {Join(users.Select(i => $"Button {i + 1}").ToList())}";
        CanRemove = users.Count == 0 && _controller.CanEdit;
        RemoveTooltip = users.Count == 0 ? null : $"{UsedByText}: put their screens on another bus first";
        RemoveCommand.NotifyCanExecuteChanged();
        HasProblem = _controller.Problems.Any(p => p.Item == Bus
            && p.Field is ConfigField.I2cMissing or ConfigField.I2cPins or ConfigField.I2cClock);
    }

    /// <summary>The draft buttons (0-based) whose screen is on <paramref name="bus"/>.</summary>
    internal static IReadOnlyList<int> ScreensOn(ControllerConfig draft, int bus)
        => Enumerable.Range(0, draft.Buttons.Count)
                     .Where(i => draft.Buttons[i].IndicatorCase == Button.IndicatorOneofCase.Screen && draft.Buttons[i].Screen.Bus == bus)
                     .ToList();

    /// <summary>"SDA 25 · SCL 26 · 400 kHz", with "–" for a pin not set: a bus's wiring in one line.</summary>
    internal static string Wiring(I2cBus? bus)
        => $"SDA {Part(bus?.HasSda == true, bus?.Sda ?? 0)} · SCL {Part(bus?.HasScl == true, bus?.Scl ?? 0)} · {(bus?.ClockHz ?? Fast) / 1000} kHz";

    private static string Part(bool has, uint pin) => has ? $"{pin}" : "–";

    /// <summary>"A", "A and B", "A, B and C".</summary>
    internal static string Join(IReadOnlyList<string> parts)
        => parts.Count <= 1 ? string.Concat(parts) : $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}";
}
