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
        _runtime.RawInput += OnRawInput;
        Refresh();
    }

    /// <summary>The draft's sliders, in draft order.</summary>
    public ObservableCollection<SliderItemViewModel> Sliders { get; } = new();

    /// <summary>The draft's buttons, in draft order.</summary>
    public ObservableCollection<ButtonItemViewModel> Buttons { get; } = new();

    /// <summary>The draft can't be edited (spec §5.4): the drawing is greyed and the inspector's
    /// fields are disabled.</summary>
    public bool IsReadOnly => !_controller.CanEdit;

    // ── Building the drawing ─────────────────────────────────────────────────────────────────

    private void OnDraftChanged(object? sender, EventArgs e) => Refresh();

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void OnControllerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ControllerViewModel.CanEdit) or null or "")
        {
            OnPropertyChanged(nameof(IsReadOnly));
            foreach (var field in _pinFields.Values) field.IsEditable = _controller.CanEdit;
        }
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

        foreach (var field in _pinFields.Values) UpdatePinField(field);
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
        _runtime.RawInput -= OnRawInput;
    }
}
