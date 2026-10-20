using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.App.ViewModels.Search;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;

namespace Volco.App.ViewModels.Hardware;

/// <summary>One row of the pin list: "GPIO 5" and its note (free, its owner, or why it can't be used).</summary>
public sealed partial class PinRowViewModel : ObservableObject
{
    internal PinRowViewModel(PinChoice choice, bool isCurrent)
    {
        Pin = choice.Pin;
        Text = $"GPIO {choice.Pin}";
        Note = choice.Note;
        IsUnusable = choice.State == PinChoiceState.Unusable;
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

    public bool IsPickable => !IsUnusable;

    /// <summary>The field's pin now.</summary>
    public bool IsCurrent { get; }

    /// <summary>The matched characters of <see cref="Text"/>.</summary>
    [ObservableProperty] private IReadOnlyList<int> _matches = [];

    /// <summary>The matched characters of <see cref="Note"/>.</summary>
    [ObservableProperty] private IReadOnlyList<int> _noteMatches = [];

    /// <summary>The keyboard selection: Enter picks it.</summary>
    [ObservableProperty] private bool _isHighlighted;
}

/// <summary>
/// The pin field (hardware-page spec §6.3): closed, the value ("GPIO 5" or "Not set"); open, a text
/// box over every GPIO the chip has, in number order, filtered with <see cref="FuzzyMatcher"/> over
/// the number and the note. Up/Down move (over unusable rows), Enter picks, Escape restores, and a
/// problem on the field shows under it with one fix where there is one ("Swap with Slider 1").
/// </summary>
public sealed partial class PinFieldViewModel : ObservableObject
{
    private readonly PinRole _role;
    private readonly PinOwner _self;
    private readonly Func<ControllerConfig> _draft;
    private readonly Func<uint?> _get;
    private readonly Action<uint?> _set;
    private readonly Action<PinOwner, PinOwner>? _swap;
    private List<PinRowViewModel> _all = new();
    private PinOwner? _other;

    /// <param name="label">"Pin", "Button pin", "Light A pin", "SDA"…</param>
    /// <param name="role">What the pin is for: which pins are unusable.</param>
    /// <param name="self">The use being edited: its own pin is not "taken".</param>
    /// <param name="draft">The draft the list is computed over, read on every open.</param>
    /// <param name="get">The field's pin in the draft.</param>
    /// <param name="set">Writes a picked pin into the draft.</param>
    /// <param name="swap">The fix of a shared pin: exchanges self's and the other use's pins.</param>
    public PinFieldViewModel(string label, PinRole role, PinOwner self, Func<ControllerConfig> draft,
                             Func<uint?> get, Action<uint?> set, Action<PinOwner, PinOwner>? swap)
    {
        Label = label;
        _role = role;
        _self = self;
        _draft = draft;
        _get = get;
        _set = set;
        _swap = swap;
        FixCommand = new RelayCommand(Fix, () => _other is not null && _swap is not null && IsEditable);
    }

    public string Label { get; }

    /// <summary>The use this field edits.</summary>
    public PinOwner Owner => _self;

    /// <summary>"GPIO 5", or "Not set".</summary>
    public string ValueText => _get() is { } pin ? $"GPIO {pin}" : "Not set";

    /// <summary>The list is open (the text box replaces the value).</summary>
    [ObservableProperty] private bool _isOpen;

    /// <summary>The search text while open.</summary>
    [ObservableProperty] private string _query = "";

    /// <summary>False while the draft can't be edited: the field is disabled.</summary>
    [ObservableProperty] private bool _isEditable = true;

    /// <summary>The problem on the field ("Slider 1 also uses GPIO 33."), or null.</summary>
    [ObservableProperty] private string? _error;

    /// <summary>The rows the query keeps, in number order (all of them for an empty query).</summary>
    public IReadOnlyList<PinRowViewModel> Rows { get; private set; } = [];

    /// <summary>The keyboard selection; never an unusable row.</summary>
    public PinRowViewModel? Highlighted { get; private set; }

    public bool HasError => Error is not null;

    /// <summary>"Swap with Slider 1", or null when the problem has no fix.</summary>
    public string? FixLabel => _other is { } o ? $"Swap with {o.Label}" : null;

    public bool HasFix => FixLabel is not null && _swap is not null;

    /// <summary>Exchanges this field's pin with the other use's (<see cref="SetProblem"/>).</summary>
    public IRelayCommand FixCommand { get; }

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnQueryChanged(string value)
    {
        if (IsOpen) Filter();
    }

    partial void OnIsEditableChanged(bool value)
    {
        if (!value && IsOpen) Cancel();
        FixCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Opens the list on every pin of the chip, the current one highlighted.</summary>
    public void Open()
    {
        if (!IsEditable) return;
        var current = _get();
        _all = PinChoices.For(_draft(), ChipInfo.Esp32, _role, _self)
            .Select(c => new PinRowViewModel(c, c.Pin == current)).ToList();
        Query = "";
        IsOpen = true;
        Filter();
        Highlight(Rows.FirstOrDefault(r => r.IsCurrent && r.IsPickable) ?? Rows.FirstOrDefault(r => r.IsPickable));
    }

    /// <summary>Picks a row (a click): sets the pin and closes. Unusable rows are ignored.</summary>
    public void Pick(PinRowViewModel row)
    {
        if (row.IsUnusable || !IsEditable) return;
        Close();
        if (_get() != row.Pin) _set(row.Pin);
        OnPropertyChanged(nameof(ValueText));
    }

    /// <summary>Escape or a click outside: the value stays as it was, the list closes.</summary>
    public void Cancel() => Close();

    /// <summary>Moves the highlight by <paramref name="step"/> pickable rows, stopping at the ends.</summary>
    public void Move(int step)
    {
        var pickable = Rows.Where(r => r.IsPickable).ToList();
        if (pickable.Count == 0) return;
        var at = Highlighted is null ? -1 : pickable.IndexOf(Highlighted);
        var next = at < 0 ? (step > 0 ? 0 : pickable.Count - 1) : Math.Clamp(at + step, 0, pickable.Count - 1);
        Highlight(pickable[next]);
    }

    /// <summary>Enter: picks the highlighted row.</summary>
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

    /// <summary>The draft changed elsewhere: re-reads the value (and the list, while open).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(ValueText));
        if (!IsOpen) return;
        var pin = Highlighted?.Pin;
        var current = _get();
        _all = PinChoices.For(_draft(), ChipInfo.Esp32, _role, _self)
            .Select(c => new PinRowViewModel(c, c.Pin == current)).ToList();
        Filter();
        if (pin is { } p) Highlight(Rows.FirstOrDefault(r => r.Pin == p && r.IsPickable) ?? Highlighted);
    }

    private void Fix()
    {
        if (_other is { } other && _swap is not null && IsEditable) _swap(_self, other);
        OnPropertyChanged(nameof(ValueText));
    }

    private void Close()
    {
        IsOpen = false;
        Query = "";
        Highlight(null);
        Rows = [];
        _all = new();
        OnPropertyChanged(nameof(Rows));
    }

    /// <summary>Keeps the rows the query matches (on the number or the note) with their highlights.
    /// The highlight stays when its row is still shown, else goes to the first pickable row.</summary>
    private void Filter()
    {
        var rows = new List<PinRowViewModel>();
        foreach (var row in _all)
        {
            if (string.IsNullOrWhiteSpace(Query))
            {
                row.Matches = [];
                row.NoteMatches = [];
                rows.Add(row);
                continue;
            }
            var text = FuzzyMatcher.Match(Query, row.Text);
            var note = text is null ? FuzzyMatcher.Match(Query, row.Note) : null;
            if (text is null && note is null) continue;
            row.Matches = text?.Indexes ?? [];
            row.NoteMatches = note?.Indexes ?? [];
            rows.Add(row);
        }
        Rows = rows;
        OnPropertyChanged(nameof(Rows));
        if (Highlighted is null || !rows.Contains(Highlighted)) Highlight(rows.FirstOrDefault(r => r.IsPickable));
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
