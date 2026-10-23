using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Communication;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;

namespace Volco.App.ViewModels.Hardware;

/// <summary>What Detect saw of a pin while the pin dialog is open: nothing, a move or press in the
/// last <see cref="PinFieldViewModel.ActivityHold"/>, or one earlier.</summary>
public enum PinActivityState { None, Now, Before }

/// <summary>One pin of the pin dialog: "GPIO 5" and its note (free, its owner, or why it can't be used).</summary>
public sealed partial class PinRowViewModel : ObservableObject
{
    internal PinRowViewModel(PinChoice choice, bool isCurrent)
    {
        Pin = choice.Pin;
        Text = $"GPIO {choice.Pin}";
        Note = choice.Note;
        IsUnusable = choice.State == PinChoiceState.Unusable;
        IsTaken = choice.State == PinChoiceState.Taken;
        IsCurrent = isCurrent;
    }

    public uint Pin { get; }

    /// <summary>"GPIO 5".</summary>
    public string Text { get; }

    /// <summary>"free", the owner ("Slider 1"), why it can't be used ("flash"), a hint, or empty for
    /// the field's own pin.</summary>
    public string Note { get; }

    /// <summary>The pin can't be used for this role: greyed, not pickable.</summary>
    public bool IsUnusable { get; }

    /// <summary>Another use of the draft has the pin; picking it is allowed (the field then shows the clash).</summary>
    public bool IsTaken { get; }

    public bool IsPickable => !IsUnusable;

    /// <summary>The field's pin now.</summary>
    public bool IsCurrent { get; }

    /// <summary>The keyboard selection: Enter picks it.</summary>
    [ObservableProperty] private bool _isHighlighted;

    /// <summary>What Detect saw of this pin since the dialog opened.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMovingNow), nameof(HasMoved))]
    private PinActivityState _activity;

    /// <summary>Moved or pressed in the last <see cref="PinFieldViewModel.ActivityHold"/>.</summary>
    public bool IsMovingNow => Activity == PinActivityState.Now;

    /// <summary>Moved or pressed earlier while the dialog was open.</summary>
    public bool HasMoved => Activity == PinActivityState.Before;
}

/// <summary>A place in the pin dialog's grid with no GPIO behind it (the chip has no pin of that number).</summary>
public sealed class PinGap
{
    public static PinGap Instance { get; } = new();
    private PinGap() { }
}

/// <summary>
/// The pin field (hardware-page spec §6.3): closed, the value ("GPIO 5" or "Not set"); open, the pin
/// dialog, a grid of every GPIO the chip has, <see cref="Columns"/> to a row in number order with a
/// gap where the chip has no pin. Arrows move (over unusable pins), Enter or a click picks, Escape
/// restores. While it is open, Detect marks the pins the controller reports (<see cref="OnPinActivity"/>)
/// without picking them. A problem on the field shows under it with one fix where there is one
/// ("Swap with Slider 1").
/// </summary>
public sealed partial class PinFieldViewModel : ObservableObject
{
    /// <summary>Pins to a row of the dialog's grid: GPIO n is in row n / 8, column n % 8.</summary>
    public const int Columns = 8;

    /// <summary>How long a reported pin stays "moving now" after its last report; then it is "moved before".</summary>
    public static TimeSpan ActivityHold { get; } = TimeSpan.FromSeconds(1);

    private readonly PinRole _role;
    private readonly PinOwner _self;
    private readonly Func<ControllerConfig> _draft;
    private readonly Func<uint?> _get;
    private readonly Action<uint?> _set;
    private readonly Action<PinOwner, PinOwner>? _swap;
    private readonly Func<PinWatchRole, IDisposable?>? _watch;
    private readonly TimeProvider _time;
    private readonly IUiDispatcher? _ui;
    private readonly Dictionary<uint, ITimer> _holds = new();
    private int _generation;   // bumped on every open and close: a hold timer of an older one does nothing
    private IDisposable? _watching;
    private PinOwner? _other;

    /// <param name="label">"Pin", "Button pin", "Light A pin", "SDA"…</param>
    /// <param name="role">What the pin is for: which pins are unusable.</param>
    /// <param name="self">The use being edited: its own pin is not "taken".</param>
    /// <param name="draft">The draft the grid is computed over, read on every open.</param>
    /// <param name="get">The field's pin in the draft.</param>
    /// <param name="set">Writes a picked pin into the draft.</param>
    /// <param name="swap">The fix of a shared pin: exchanges self's and the other use's pins.</param>
    /// <param name="watch">Starts the controller's pin watch for Detect while the dialog is open; null for
    /// none. The result is disposed on close.</param>
    /// <param name="time">Times "moving now" (<see cref="ActivityHold"/>).</param>
    /// <param name="ui">Where the hold's end runs; null runs it on the timer's thread.</param>
    public PinFieldViewModel(string label, PinRole role, PinOwner self, Func<ControllerConfig> draft,
                             Func<uint?> get, Action<uint?> set, Action<PinOwner, PinOwner>? swap,
                             Func<PinWatchRole, IDisposable?>? watch = null, TimeProvider? time = null, IUiDispatcher? ui = null)
    {
        Label = label;
        _role = role;
        _self = self;
        _draft = draft;
        _get = get;
        _set = set;
        _swap = swap;
        _watch = watch;
        _time = time ?? TimeProvider.System;
        _ui = ui;
        FixCommand = new RelayCommand(Fix, () => _other is not null && _swap is not null && IsEditable);
    }

    public string Label { get; }

    /// <summary>The use this field edits.</summary>
    public PinOwner Owner => _self;

    /// <summary>The dialog's title: "Slider 1 pin", "Button 2 light A pin", "I2C bus 0 SDA pin".</summary>
    public string Title => $"{_self.Label} pin";

    /// <summary>"GPIO 5", or "Not set".</summary>
    public string ValueText => _get() is { } pin ? $"GPIO {pin}" : "Not set";

    /// <summary>The pin dialog is open.</summary>
    [ObservableProperty] private bool _isOpen;

    /// <summary>False while the draft can't be edited: the field is disabled.</summary>
    [ObservableProperty] private bool _isEditable = true;

    /// <summary>The problem on the field ("Slider 1 also uses GPIO 33."), or null.</summary>
    [ObservableProperty] private string? _error;

    /// <summary>What Detect asks of the user while the dialog watches ("Move a slider…"), else null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWatchHint))]
    private string? _watchHint;

    public bool HasWatchHint => WatchHint is not null;

    /// <summary>Every pin of the chip, in number order (empty while closed).</summary>
    public IReadOnlyList<PinRowViewModel> Rows { get; private set; } = [];

    /// <summary>The grid's places, <see cref="Columns"/> to a row: <see cref="Rows"/> by number, with a
    /// <see cref="PinGap"/> for each number the chip lacks.</summary>
    public IReadOnlyList<object> Cells { get; private set; } = [];

    /// <summary>The keyboard selection; never an unusable pin.</summary>
    public PinRowViewModel? Highlighted { get; private set; }

    public bool HasError => Error is not null;

    /// <summary>"Swap with Slider 1", or null when the problem has no fix.</summary>
    public string? FixLabel => _other is { } o ? $"Swap with {o.Label}" : null;

    public bool HasFix => FixLabel is not null && _swap is not null;

    /// <summary>Exchanges this field's pin with the other use's (<see cref="SetProblem"/>).</summary>
    public IRelayCommand FixCommand { get; }

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnIsEditableChanged(bool value)
    {
        if (!value && IsOpen) Cancel();
        FixCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Opens the dialog on every pin of the chip, the current one highlighted, and starts Detect.</summary>
    public void Open()
    {
        if (!IsEditable || IsOpen) return;
        _generation++;
        SetRows(PinChoices.For(_draft(), ChipInfo.Esp32, _role, _self)
            .Select(c => new PinRowViewModel(c, c.Pin == _get())).ToList());
        Highlight(Rows.FirstOrDefault(r => r.IsCurrent && r.IsPickable) ?? Rows.FirstOrDefault(r => r.IsPickable));
        _watching = WatchRole() is { } role ? _watch?.Invoke(role) : null;
        WatchHint = _watching is null ? null
            : _role == PinRole.SliderInput ? "Move a slider to see which pin it's on."
            : "Press a button to see which pin it's on.";
        IsOpen = true;
    }

    /// <summary>Detect: the controller saw a pin move or be pressed. Its pin is marked "moving now"
    /// until <see cref="ActivityHold"/> passes without another report, then "moved before"; the
    /// selection doesn't move. Ignored while closed, without a watch, or for a pin the chip doesn't list.</summary>
    public void OnPinActivity(uint pin)
    {
        if (!IsOpen || _watching is null) return;
        var row = Rows.FirstOrDefault(r => r.Pin == pin);
        if (row is null) return;
        row.Activity = PinActivityState.Now;
        if (_holds.Remove(pin, out var old)) old.Dispose();
        var generation = _generation;
        ITimer? timer = null;
        timer = _time.CreateTimer(_ => Post(() =>
        {
            if (generation != _generation || !_holds.TryGetValue(pin, out var current) || !ReferenceEquals(current, timer)) return;
            _holds.Remove(pin);
            timer!.Dispose();
            row.Activity = PinActivityState.Before;
        }), null, ActivityHold, Timeout.InfiniteTimeSpan);
        _holds[pin] = timer;
    }

    private void Post(Action action)
    {
        if (_ui is null) action();
        else _ui.Post(action);
    }

    private PinWatchRole? WatchRole() => _role switch
    {
        PinRole.SliderInput => PinWatchRole.Analog,
        PinRole.ButtonInput => PinWatchRole.Button,
        _ => null,
    };

    /// <summary>Picks a pin (a click): sets it and closes. Unusable pins are ignored.</summary>
    public void Pick(PinRowViewModel row)
    {
        if (row.IsUnusable || !IsEditable) return;
        Close();
        if (_get() != row.Pin) _set(row.Pin);
        OnPropertyChanged(nameof(ValueText));
    }

    /// <summary>Escape, Cancel or a click on the scrim: the value stays as it was, the dialog closes.</summary>
    public void Cancel() => Close();

    /// <summary>Moves the selection by <paramref name="step"/> pin numbers (±1 along a row,
    /// ±<see cref="Columns"/> down or up a column), on to the next pickable pin that way; it stays
    /// put when there is none.</summary>
    public void Move(int step)
    {
        if (step == 0 || Rows.Count == 0) return;
        var byPin = Rows.Where(r => r.IsPickable).ToDictionary(r => (long)r.Pin);
        if (Highlighted is null)
        {
            Highlight(step > 0 ? Rows.FirstOrDefault(r => r.IsPickable) : Rows.LastOrDefault(r => r.IsPickable));
            return;
        }
        var last = (long)Rows[^1].Pin;
        for (var pin = (long)Highlighted.Pin + step; pin >= 0 && pin <= last; pin += step)
            if (byPin.TryGetValue(pin, out var row))
            {
                Highlight(row);
                return;
            }
    }

    /// <summary>Enter: picks the selected pin.</summary>
    public void Commit()
    {
        if (Highlighted is { } row) Pick(row);
    }

    /// <summary>Shows a problem on the field (null clears it). <paramref name="other"/> is the other
    /// use of a shared pin, when swapping with it fixes the problem.</summary>
    public void SetProblem(string? message, PinOwner? other)
    {
        Error = message;
        _other = message is null ? null : other;
        OnPropertyChanged(nameof(FixLabel));
        OnPropertyChanged(nameof(HasFix));
        FixCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The draft changed elsewhere: re-reads the value (and the grid, while open, keeping
    /// the selection and what Detect saw).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(ValueText));
        if (!IsOpen) return;
        var pin = Highlighted?.Pin;
        var seen = Rows.Where(r => r.Activity != PinActivityState.None).ToDictionary(r => r.Pin, r => r.Activity);
        var rows = PinChoices.For(_draft(), ChipInfo.Esp32, _role, _self)
            .Select(c => new PinRowViewModel(c, c.Pin == _get())).ToList();
        foreach (var row in rows)
            if (seen.TryGetValue(row.Pin, out var activity)) row.Activity = activity;
        SetRows(rows);
        Highlighted = null;
        Highlight(rows.FirstOrDefault(r => r.Pin == pin && r.IsPickable) ?? rows.FirstOrDefault(r => r.IsPickable));
    }

    private void Fix()
    {
        if (_other is { } other && _swap is not null && IsEditable) _swap(_self, other);
        OnPropertyChanged(nameof(ValueText));
    }

    private void Close()
    {
        _generation++;
        _watching?.Dispose();
        _watching = null;
        foreach (var timer in _holds.Values) timer.Dispose();
        _holds.Clear();
        WatchHint = null;
        IsOpen = false;
        Highlight(null);
        SetRows([]);
    }

    private void SetRows(List<PinRowViewModel> rows)
    {
        Rows = rows;
        var cells = new List<object>();
        if (rows.Count > 0)
        {
            var byPin = rows.ToDictionary(r => r.Pin);
            var count = ((int)rows[^1].Pin / Columns + 1) * Columns;
            for (uint pin = 0; pin < count; pin++)
                cells.Add(byPin.TryGetValue(pin, out var row) ? row : PinGap.Instance);
        }
        Cells = cells;
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(Cells));
    }

    private void Highlight(PinRowViewModel? row)
    {
        if (ReferenceEquals(row, Highlighted)) return;
        if (Highlighted is not null) Highlighted.IsHighlighted = false;
        Highlighted = row;
        if (row is not null) row.IsHighlighted = true;
        OnPropertyChanged(nameof(Highlighted));
    }
}
