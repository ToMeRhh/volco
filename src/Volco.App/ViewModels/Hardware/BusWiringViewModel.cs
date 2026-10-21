using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;

namespace Volco.App.ViewModels.Hardware;

/// <summary>
/// One I2C bus a screen uses, as a row of Controller settings › Screens (controller-page spec §4.3):
/// the summary "I2C bus 0 · SDA 25 · SCL 26 · 400 kHz", opened to its SDA and SCL pins and its clock
/// (100 or 400 kHz). A clock set elsewhere to another value shows no choice and is kept.
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
        ToggleCommand = new RelayCommand(() => IsOpen = !IsOpen);
        Refresh();
    }

    /// <summary>The bus's hardware number (0-3).</summary>
    public int Bus { get; }

    /// <summary>"I2C bus N".</summary>
    public string Title => $"I2C bus {Bus}";

    /// <summary>"I2C bus 0 · SDA 25 · SCL 26 · 400 kHz".</summary>
    [ObservableProperty] private string _summary = "";

    /// <summary>The row shows its pins and clock.</summary>
    [ObservableProperty] private bool _isOpen;

    /// <summary>The draft has a problem with this bus: the row's danger mark, and it opens itself.</summary>
    [ObservableProperty] private bool _hasProblem;

    /// <summary>The row's header click.</summary>
    public IRelayCommand ToggleCommand { get; }

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
        var bus = Message;
        var clock = (bus?.ClockHz ?? Fast) / 1000;
        Summary = $"{Title} · SDA {Part(bus?.HasSda == true, bus?.Sda ?? 0)} · SCL {Part(bus?.HasScl == true, bus?.Scl ?? 0)} · {clock} kHz";
        HasProblem = _controller.Problems.Any(p => p.Item == Bus
            && p.Field is ConfigField.I2cMissing or ConfigField.I2cPins or ConfigField.I2cClock);
    }

    private static string Part(bool has, uint pin) => has ? $"{pin}" : "–";
}
