using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;

namespace Volco.App.ViewModels.Hardware;

/// <summary>
/// One I2C bus in the I2C buses dialog (controller-page spec §4.3): "Bus 0 · Hardware I2C", which
/// screens use it, its SDA and SCL pins and its clock (100 or 400 kHz), and Remove, which only a bus
/// no screen uses has. A clock set elsewhere to another value shows no choice and is kept. Find screens
/// (hardware-page spec §6.4) scans the draft's pins, saved or not, and lists the addresses that answer.
/// </summary>
public sealed partial class BusWiringViewModel : ObservableObject
{
    private const uint Slow = 100_000;
    private const uint Fast = 400_000;

    private readonly ControllerViewModel _controller;
    private readonly Func<int, int, CancellationToken, Task<IReadOnlyList<int>?>>? _scan;
    private IReadOnlyList<int>? _answers;        // the last scan's addresses, null before one
    private (int Sda, int Scl) _scanned;         // the pins that scan used
    private bool _listed;                        // the last scan has rows to list (it was answered)

    /// <param name="scan">Scans a bus's pins for I2C addresses (null for no answer); null hides the button's use.</param>
    internal BusWiringViewModel(int bus, ControllerViewModel controller, PinFieldViewModel sda, PinFieldViewModel scl, Action<int> remove,
                                Func<int, int, CancellationToken, Task<IReadOnlyList<int>?>>? scan = null)
    {
        Bus = bus;
        _controller = controller;
        _scan = scan;
        Sda = sda;
        Scl = scl;
        RemoveCommand = new RelayCommand(() => remove(Bus), () => CanRemove);
        FindScreensCommand = new AsyncRelayCommand(FindScreensAsync, CanFind);
        Refresh();
    }

    /// <summary>Scans the draft's SDA and SCL for I2C addresses. Enabled with a scanner, a ready
    /// controller, no test, both pins set and no scan running.</summary>
    public IAsyncRelayCommand FindScreensCommand { get; }

    /// <summary>The last scan's rows: the addresses that answered in order, then the configured
    /// screens of this bus that did not. Empty before a scan and after a pin change.</summary>
    public IReadOnlyList<ScreenFoundViewModel> Found { get; private set; } = [];

    /// <summary>There are rows to list.</summary>
    public bool HasFound => Found.Count > 0;

    /// <summary>null before a scan; "No screens answered.", "The controller didn't answer." or
    /// "Save first to scan these pins.".</summary>
    [ObservableProperty] private string? _findResult;

    private (int Sda, int Scl)? Pins => Message is { HasSda: true, HasScl: true } b ? ((int)b.Sda, (int)b.Scl) : null;

    private bool CanFind() => _scan is not null && _controller.IsControllerReady && !_controller.IsTesting && Pins is not null;

    private async Task FindScreensAsync()
    {
        if (_scan is null || Pins is not { } pins) return;
        _scanned = pins;
        _answers = null;
        _listed = false;
        Found = [];
        FindResult = null;
        OnPropertyChanged(nameof(Found)); OnPropertyChanged(nameof(HasFound));
        if (ClashesWithSaved(pins))
        {
            _answers = [];
            FindResult = "Save first to scan these pins.";
            return;
        }
        var answers = await _scan(pins.Sda, pins.Scl, CancellationToken.None);
        if (Pins != pins) return;   // the pins moved during the scan
        _answers = answers ?? [];
        _listed = answers is not null;
        FindResult = answers is null ? "The controller didn't answer." : answers.Count == 0 ? "No screens answered." : null;
        UpdateFound();
    }

    /// <summary>The pair shares a pin with a bus a screen uses in the saved setup without being one
    /// of them: the controller refuses to scan it (it answers no addresses) until the draft is saved.</summary>
    private bool ClashesWithSaved((int Sda, int Scl) pins)
    {
        if (_controller.DecodeMirror() is not { } saved) return false;
        var used = new List<(int Sda, int Scl)>();
        for (int bus = 0; bus < 4; bus++)
            if (saved.Buttons.Any(x => x.IndicatorCase == Button.IndicatorOneofCase.Screen && x.Screen.Bus == (uint)bus)
                && ControllerDraft.Bus(saved, bus) is { HasSda: true, HasScl: true } b)
                used.Add(((int)b.Sda, (int)b.Scl));
        if (used.Contains(pins)) return false;
        return used.Any(u => u.Sda == pins.Sda || u.Sda == pins.Scl || u.Scl == pins.Sda || u.Scl == pins.Scl);
    }

    private void UpdateFound()
    {
        var screens = new List<(int Address, int Button)>();
        var buttons = _controller.Draft.Buttons;
        for (int i = 0; i < buttons.Count; i++)
            if (buttons[i].IndicatorCase == Button.IndicatorOneofCase.Screen && buttons[i].Screen.Bus == (uint)Bus)
                screens.Add(((int)buttons[i].Screen.Address, i));
        string UsedBy(int address)
        {
            var users = screens.Where(s => s.Address == address).Select(s => s.Button + 1).ToList();
            return users.Count == 0 ? "" : users.Count == 1 ? $"Button {users[0]}" : $"Buttons {string.Join(", ", users)}";
        }
        if (!_listed)
        {
            Found = [];
            OnPropertyChanged(nameof(Found)); OnPropertyChanged(nameof(HasFound));
            return;
        }
        var answered = _answers ?? [];
        Found = answered.Order().Select(a => new ScreenFoundViewModel(a, UsedBy(a), false))
            .Concat(screens.Select(s => s.Address).Distinct().Where(a => !answered.Contains(a)).Order()
                .Select(a => new ScreenFoundViewModel(a, UsedBy(a), true))).ToList();
        OnPropertyChanged(nameof(Found)); OnPropertyChanged(nameof(HasFound));
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
        if (_answers is not null)
        {
            if (Pins != _scanned) { _answers = null; _listed = false; FindResult = null; Found = []; OnPropertyChanged(nameof(Found)); OnPropertyChanged(nameof(HasFound)); }
            else UpdateFound();
        }
        FindScreensCommand.NotifyCanExecuteChanged();
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

/// <summary>One row of Find screens: an address that answered, or a configured screen that did not.</summary>
public sealed class ScreenFoundViewModel
{
    internal ScreenFoundViewModel(int address, string usedBy, bool isMissing)
    {
        Address = $"0x{address:X2}";
        UsedBy = usedBy;
        IsMissing = isMissing;
    }

    /// <summary>"0x3C".</summary>
    public string Address { get; }

    /// <summary>"Button 2", "Buttons 2, 4" or "" when no button uses it.</summary>
    public string UsedBy { get; }

    /// <summary>A screen of this bus is configured at this address and did not answer.</summary>
    public bool IsMissing { get; }

    /// <summary>The row's second column: "Screen found, used by Button 2", "Screen found, no button
    /// uses it", or for a missing screen "Button 3's screen didn't answer".</summary>
    public string Text => IsMissing
        ? UsedBy.Contains(',') ? $"The screen of {UsedBy} didn't answer" : $"{UsedBy}'s screen didn't answer"
        : UsedBy.Length > 0 ? $"Screen found, used by {UsedBy}" : "Screen found, no button uses it";
}
