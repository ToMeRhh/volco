using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Volco.Core.Runtime;

namespace Volco.App.ViewModels.Hardware;

/// <summary>
/// The Hardware page's drawing (spec docs/design/hardware-page.md §5): the draft's controls in
/// draft order, each the Controls item its <see cref="ControlLayout"/> source names (so a slider
/// that moved down keeps its target) or a detached item for an added one, with its wiring captions
/// (<see cref="TileWiring"/>) and the live raw input. Every Controls item has a wiring from the start,
/// so tiles keep their size when the stage switches to Hardware. UI thread; runtime events are posted.
/// </summary>
public sealed partial class HardwareViewModel : ObservableObject, IDisposable
{
    private const double MaxRaw = 4095.0;

    private readonly ControllerViewModel _controller;
    private readonly ControlsViewModel _controls;
    private readonly VolcoRuntime _runtime;
    private readonly IUiDispatcher _ui;
    private bool _disposed;

    public HardwareViewModel(ControllerViewModel controller, ControlsViewModel controls, VolcoRuntime runtime, IUiDispatcher ui)
    {
        _controller = controller;
        _controls = controls;
        _runtime = runtime;
        _ui = ui;
        _controller.DraftChanged += OnDraftChanged;
        _controller.PropertyChanged += OnControllerPropertyChanged;
        _controls.Sliders.Items.CollectionChanged += OnItemsChanged;
        _controls.Buttons.Items.CollectionChanged += OnItemsChanged;
        _controls.PropertyChanged += OnControlsPropertyChanged;
        _runtime.RawInput += OnRawInput;
        Card = new DeviceCardViewModel(controller, this);
        Refresh();
    }

    /// <summary>The device card at the top of the side panel (spec §4).</summary>
    public DeviceCardViewModel Card { get; }

    // ── What the card opens (spec §4.2, §7, §10); the views attach in later steps ──────────────

    /// <summary>Compare (spec §7.2) is open.</summary>
    [ObservableProperty] private bool _isCompareOpen;

    /// <summary>The Connection dialog (spec §10) is open.</summary>
    [ObservableProperty] private bool _isConnectionDialogOpen;

    /// <summary>The Connection dialog opens on its Wi-Fi tab, else on USB.</summary>
    [ObservableProperty] private bool _connectionDialogOnWifi;

    /// <summary>The setup chooser (spec §7.1) fills the drawing.</summary>
    [ObservableProperty] private bool _showChooser;

    /// <summary>The card's Compare…: the controller's setup beside Volco's (spec §7.2).</summary>
    public void OpenCompare() => IsCompareOpen = true;

    /// <summary>The card's status line and ⋯ Connection…: the Connection dialog, on the Wi-Fi tab
    /// when <paramref name="wifi"/>.</summary>
    public void OpenConnection(bool wifi)
    {
        ConnectionDialogOnWifi = wifi;
        IsConnectionDialogOpen = true;
    }

    /// <summary>⋯ Start over…: the drawing returns to the setup chooser; the draft stays until a
    /// choice is made.</summary>
    public void StartOver() => ShowChooser = true;

    /// <summary>The draft's sliders, in draft order.</summary>
    public ObservableCollection<SliderItemViewModel> Sliders { get; } = new();

    /// <summary>The draft's buttons, in draft order.</summary>
    public ObservableCollection<ButtonItemViewModel> Buttons { get; } = new();

    /// <summary>The draft can't be edited (spec §5.4): the drawing is greyed and the inspector's
    /// fields are disabled.</summary>
    public bool IsReadOnly => !_controller.CanEdit;

    /// <summary>One chip per I2C bus that a screen uses, in bus order (spec §5.2).</summary>
    public ObservableCollection<BusChipViewModel> BusChips { get; } = new();

    /// <summary>A screen uses a bus: the chips' row is shown (its space is kept in both modes).</summary>
    public bool HasBusChips => BusChips.Count > 0;

    /// <summary>
    /// What the inspector shows (spec §5.3, §6): the <see cref="SliderWiringViewModel"/> or
    /// <see cref="ButtonWiringViewModel"/> of the tile selected (the selection is Controls', so it
    /// carries over), or the selected bus's <see cref="BusWiringViewModel"/>; null when the selection
    /// is not in the drawing.
    /// </summary>
    [ObservableProperty] private object? _inspected;

    /// <summary>The question asked inline ("Remove Slider 3?"), or null.</summary>
    public Confirmation? Confirmation => _controller.Confirmation;

    // ── Building the drawing ─────────────────────────────────────────────────────────────────

    private void OnDraftChanged(object? sender, EventArgs e) => Refresh();

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void OnControllerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ControllerViewModel.CanEdit) or null or "")
        {
            OnPropertyChanged(nameof(IsReadOnly));
            foreach (var field in _pinFields.Values) field.IsEditable = _controller.CanEdit;
            RefreshInspected();
        }
        if (e.PropertyName is nameof(ControllerViewModel.Confirmation) or null or "") OnPropertyChanged(nameof(Confirmation));
        if (e.PropertyName == nameof(ControllerViewModel.Layout)) Refresh();
    }

    private void Refresh()
    {
        if (_disposed) return;
        var draft = _controller.Draft;
        var layout = _controller.Layout;
        var baseline = _controller.BaselineConfig();
        var problems = _controller.Problems;

        Sync(Sliders, _controls.Sliders.Items, draft.Sliders.Count, layout.Sliders, _controls.Sliders.CreateDetached);
        Sync(Buttons, _controls.Buttons.Items, draft.Buttons.Count, layout.Buttons, _controls.Buttons.CreateDetached);

        for (int i = 0; i < Sliders.Count; i++)
        {
            var w = Sliders[i].Wiring ??= new TileWiring();
            var slider = draft.Sliders[i];
            w.Title = $"Slider {i + 1}";
            w.PinText = PinText(slider.HasPin, slider.Pin);
            w.IndicatorText = "";
            w.HasChange = !Equals(Saved(baseline.Sliders, Source(layout.Sliders, i)), slider);
            w.Problem = ProblemOf(problems, i, f => f == ConfigField.SliderPin);
        }
        for (int i = 0; i < Buttons.Count; i++)
        {
            var w = Buttons[i].Wiring ??= new TileWiring();
            var button = draft.Buttons[i];
            w.Title = $"Button {i + 1}";
            w.PinText = PinText(button.HasPin, button.Pin);
            w.IndicatorText = IndicatorText(button);
            w.HasChange = !Equals(Saved(baseline.Buttons, Source(layout.Buttons, i)), button);
            w.Problem = ProblemOf(problems, i, f => f is ConfigField.ButtonPin or ConfigField.LedPin or ConfigField.PairPinA
                                                   or ConfigField.PairPinB or ConfigField.ScreenBus or ConfigField.ScreenAddress);
        }

        // A Controls tile the draft doesn't draw still measures its wiring layer.
        foreach (var item in _controls.Sliders.Items) item.Wiring ??= new TileWiring { Title = item.Heading };
        foreach (var item in _controls.Buttons.Items) item.Wiring ??= new TileWiring { Title = item.Heading };

        SyncBusChips(draft);
        foreach (var field in _pinFields.Values) UpdatePinField(field);
        UpdateInspected();
    }

    // ── The inspector and the bus chips (spec §5.3, §6.1, §6.2, §6.4) ───────────────────────

    /// <summary>The bus the inspector shows; null when it shows the selected tile.</summary>
    private int? _selectedBus;

    /// <summary>The inspector shows the controller's settings (spec §6.6).</summary>
    private bool _settingsSelected;

    /// <summary>The title row's "Controller settings" chip: the inspector shows the controller's
    /// settings and the tile and bus selection is cleared.</summary>
    public void SelectControllerSettings()
    {
        if (_disposed) return;
        _settingsSelected = true;
        _selectedBus = null;
        _controls.Selected = null;
        UpdateInspected();
    }

    /// <summary>Selects bus <paramref name="bus"/>'s chip: the tile selection is cleared.</summary>
    public void SelectBus(int bus)
    {
        if (_disposed || BusChips.All(c => c.Bus != bus)) return;
        _selectedBus = bus;
        _settingsSelected = false;
        _controls.Selected = null;
        UpdateInspected();
    }

    private void OnControlsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || e.PropertyName != nameof(ControlsViewModel.Selected)) return;
        if (_controls.Selected is not null) { _selectedBus = null; _settingsSelected = false; }
        UpdateInspected();
    }

    private void SyncBusChips(ControllerConfig draft)
    {
        var used = draft.Buttons.Where(b => b.IndicatorCase == Button.IndicatorOneofCase.Screen)
                                .Select(b => (int)b.Screen.Bus).Distinct().Order().ToList();
        if (!BusChips.Select(c => c.Bus).SequenceEqual(used))
        {
            var old = BusChips.ToDictionary(c => c.Bus);
            BusChips.Clear();
            foreach (var bus in used) BusChips.Add(old.TryGetValue(bus, out var chip) ? chip : new BusChipViewModel(bus, SelectBus));
            OnPropertyChanged(nameof(HasBusChips));
        }
        foreach (var chip in BusChips)
        {
            var bus = ControllerDraft.Bus(draft, chip.Bus);
            chip.Text = $"I2C bus {chip.Bus} · SDA {Part(bus?.HasSda == true, bus?.Sda ?? 0)} · SCL {Part(bus?.HasScl == true, bus?.Scl ?? 0)}";
        }
    }

    /// <summary>Points <see cref="Inspected"/> at the selection, keeping its view model while it
    /// is the same control at the same place, and marks the selected chip.</summary>
    private void UpdateInspected()
    {
        if (_selectedBus is int b && BusChips.All(c => c.Bus != b)) _selectedBus = null;
        foreach (var chip in BusChips) chip.IsSelected = chip.Bus == _selectedBus;

        object? next = Inspected;
        if (_settingsSelected)
        {
            if (Inspected is not ControllerSettingsViewModel) next = new ControllerSettingsViewModel(_controller, () => OpenConnection(true));
        }
        else if (_selectedBus is int bus)
        {
            if (Inspected is not BusWiringViewModel { } shown || shown.Bus != bus)
                next = new BusWiringViewModel(bus, _controller, PinField(new PinOwner(PinOwnerKind.BusSda, bus)),
                                              PinField(new PinOwner(PinOwnerKind.BusScl, bus)));
        }
        else if (_controls.Selected is SliderItemViewModel slider && Sliders.IndexOf(slider) is >= 0 and var si)
        {
            if (Inspected is not SliderWiringViewModel { } shown || shown.Index != si || !ReferenceEquals(shown.Item, slider))
                next = new SliderWiringViewModel(si, slider, PinField(new PinOwner(PinOwnerKind.Slider, si)), () => AskRemove(slider));
        }
        else if (_controls.Selected is ButtonItemViewModel button && Buttons.IndexOf(button) is >= 0 and var bi)
        {
            if (Inspected is not ButtonWiringViewModel { } shown || shown.Index != bi || !ReferenceEquals(shown.Item, button))
                next = new ButtonWiringViewModel(bi, button, _controller, kind => PinField(new PinOwner(kind, bi)), () => AskRemove(button));
        }
        else next = null;

        if (!ReferenceEquals(next, Inspected))
        {
            (Inspected as IDisposable)?.Dispose();
            Inspected = next;
        }
        RefreshInspected();
    }

    private void RefreshInspected()
    {
        switch (Inspected)
        {
            case SliderWiringViewModel s: s.IsEditable = _controller.CanEdit; break;
            case ButtonWiringViewModel b: b.Refresh(); break;
            case BusWiringViewModel bus: bus.Refresh(); break;
        }
    }

    /// <summary>"Remove Slider N?"; once removed, the tile now at its place (else the one before)
    /// is selected.</summary>
    private void AskRemove(ObservableObject item)
    {
        if (item is SliderItemViewModel slider && Sliders.IndexOf(slider) is >= 0 and var si)
            _controller.AskRemoveSlider(si, () => SelectAfterRemove(Sliders, si));
        else if (item is ButtonItemViewModel button && Buttons.IndexOf(button) is >= 0 and var bi)
            _controller.AskRemoveButton(bi, () => SelectAfterRemove(Buttons, bi));
    }

    private void SelectAfterRemove<T>(IList<T> tiles, int index) where T : ObservableObject
    {
        if (tiles.Count > 0) _controls.Select(tiles[Math.Min(index, tiles.Count - 1)]);
        else _controls.Selected = null;
    }

    // ── Pin fields (spec §6.3) ───────────────────────────────────────────────────────────────

    private readonly Dictionary<PinOwner, PinFieldViewModel> _pinFields = new();

    /// <summary>The last pick, with the pin its field had before: a pick that took another use's pin
    /// is undone by giving that use the old pin (the swap).</summary>
    private (PinOwner Owner, uint? Before)? _lastPick;

    /// <summary>
    /// The pin field of one use (a slider's, a button's, a light's, a bus's SDA or SCL), made once
    /// per use and kept current after every draft change: its value, whether it can be edited, and
    /// its problem with the swap fix (<see cref="DraftPins.ProblemFor"/>). The inspectors bind these.
    /// </summary>
    public PinFieldViewModel PinField(PinOwner owner)
    {
        if (_pinFields.TryGetValue(owner, out var field)) return field;
        field = new PinFieldViewModel(DraftPins.LabelOf(owner.Kind), DraftPins.RoleOf(owner.Kind), owner,
                                      () => _controller.Draft, () => DraftPins.Get(_controller.Draft, owner),
                                      pin => SetPin(owner, pin), SwapPins);
        _pinFields[owner] = field;
        UpdatePinField(field);
        return field;
    }

    private void UpdatePinField(PinFieldViewModel field)
    {
        field.IsEditable = _controller.CanEdit;
        field.Refresh();
        var (message, other) = DraftPins.ProblemFor(_controller.Draft, _controller.Problems, field.Owner);
        field.SetProblem(message, other is not null && CanSwap(field.Owner, other) ? other : null);
    }

    private void SetPin(PinOwner owner, uint? pin)
    {
        if (!_controller.CanEdit) return;
        var draft = _controller.Draft;
        _lastPick = (owner, DraftPins.Get(draft, owner));
        DraftPins.Set(draft, owner, pin);
        _controller.OnDraftEdited();
    }

    /// <summary>The fix of a shared pin: <paramref name="a"/> and <paramref name="b"/> exchange
    /// pins. When they hold the same one because one of them just took it, that one keeps it and
    /// the other gets the pin it had before.</summary>
    private void SwapPins(PinOwner a, PinOwner b)
    {
        if (!_controller.CanEdit) return;
        var draft = _controller.Draft;
        if (DraftPins.Get(draft, a) == DraftPins.Get(draft, b))
        {
            if (_lastPick is not { Before: { } before } pick || (pick.Owner != a && pick.Owner != b)) return;
            DraftPins.Set(draft, pick.Owner == a ? b : a, before);
        }
        else DraftPins.Swap(draft, a, b);
        _lastPick = null;
        _controller.OnDraftEdited();
    }

    private bool CanSwap(PinOwner a, PinOwner b)
    {
        var draft = _controller.Draft;
        if (DraftPins.Get(draft, a) != DraftPins.Get(draft, b)) return true;
        return _lastPick is { Before: { } before } pick && (pick.Owner == a || pick.Owner == b)
               && before != DraftPins.Get(draft, pick.Owner);
    }

    /// <summary>Makes <paramref name="target"/> the draft's controls: the saved item each source
    /// names, else a detached one (the previous detached items are reused in order).</summary>
    private static void Sync<T>(ObservableCollection<T> target, IList<T> saved, int count, IReadOnlyList<int?> sources,
                                Func<int, T> createDetached) where T : class
    {
        var detached = new Queue<T>(target.Where(t => !saved.Contains(t)));
        var wanted = new List<T>(count);
        for (int i = 0; i < count; i++)
        {
            if (Source(sources, i) is int s && s < saved.Count) wanted.Add(saved[s]);
            else wanted.Add(detached.Count > 0 ? detached.Dequeue() : createDetached(i));
        }
        if (target.SequenceEqual(wanted)) return;
        target.Clear();
        foreach (var item in wanted) target.Add(item);
    }

    private static int? Source(IReadOnlyList<int?> sources, int i) => i < sources.Count ? sources[i] : null;

    private static T? Saved<T>(IList<T> baseline, int? source) where T : class
        => source is int s && s < baseline.Count ? baseline[s] : null;

    private static string PinText(bool has, uint pin) => has ? $"GPIO {pin}" : "Not set";

    /// <summary>"12", "18 · 19" or "Bus 0 · 3C"; empty for no indicator.</summary>
    private static string IndicatorText(Button button) => button.IndicatorCase switch
    {
        Button.IndicatorOneofCase.Led => button.Led.HasPin ? $"{button.Led.Pin}" : "Not set",
        Button.IndicatorOneofCase.LedPair => button.LedPair is { HasPinA: false, HasPinB: false } ? "Not set"
            : $"{Part(button.LedPair.HasPinA, button.LedPair.PinA)} · {Part(button.LedPair.HasPinB, button.LedPair.PinB)}",
        Button.IndicatorOneofCase.Screen => $"Bus {button.Screen.Bus} · {button.Screen.Address:X2}",
        _ => "",
    };

    private static string Part(bool has, uint pin) => has ? $"{pin}" : "–";

    private static string? ProblemOf(IReadOnlyList<DraftProblem> problems, int item, Func<ConfigField, bool> field)
    {
        var mine = problems.Where(p => p.Item == item && field(p.Field)).Select(p => p.Message).Distinct().ToList();
        return mine.Count == 0 ? null : string.Join("\n", mine);
    }

    // ── Raw input (spec §5.2: the fader shows the live raw level, a press flashes the tile) ──

    private void OnRawInput(object? sender, RawInputEventArgs e) => _ui.Post(() =>
    {
        if (_disposed) return;
        var layout = _controller.Layout;
        if (e.Kind == RawInputKind.Slider && Find(Sliders, layout.Sliders, e.Index) is { } slider)
            slider.Wiring!.RawLevel = Math.Clamp(e.Value / MaxRaw, 0, 1);
        else if (e.Kind == RawInputKind.Button && e.Value != 0 && Find(Buttons, layout.Buttons, e.Index) is { } button)
            button.Pulse();
    });

    /// <summary>The tile of hardware input <paramref name="input"/>: the draft control whose source it is.</summary>
    private static T? Find<T>(IList<T> tiles, IReadOnlyList<int?> sources, int input) where T : class
    {
        for (int i = 0; i < tiles.Count && i < sources.Count; i++)
            if (sources[i] == input) return tiles[i];
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _controller.DraftChanged -= OnDraftChanged;
        _controller.PropertyChanged -= OnControllerPropertyChanged;
        _controls.Sliders.Items.CollectionChanged -= OnItemsChanged;
        _controls.Buttons.Items.CollectionChanged -= OnItemsChanged;
        _controls.PropertyChanged -= OnControlsPropertyChanged;
        _runtime.RawInput -= OnRawInput;
        Card.Dispose();
        (Inspected as IDisposable)?.Dispose();
    }
}
