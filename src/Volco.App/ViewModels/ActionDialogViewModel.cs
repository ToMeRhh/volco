using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.App.ViewModels.Search;
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Input;

namespace Volco.App.ViewModels;

/// <summary>One action in the action dialog's list. <see cref="Highlight"/> holds the positions in
/// <see cref="Name"/> that matched the query.</summary>
public sealed record ActionItem(ButtonActionType Type, string Name, IReadOnlyList<int> Highlight, bool IsEnabled)
{
    /// <summary>The draft's action.</summary>
    public bool IsPicked { get; init; }

    /// <summary>Where Up and Down are; Enter picks it.</summary>
    public bool IsCurrent { get; init; }

    /// <summary>The <see cref="ButtonActionType"/> name, for its icon.</summary>
    public string IconKey => Type.ToString();
}

/// <summary>A section of the action dialog's list. While searching it is open whatever its
/// remembered state, holds only its matches by score, and shows <see cref="MatchCount"/>.</summary>
public sealed record ActionSection(string Name, bool IsCollapsed, IReadOnlyList<ActionItem> Items, int MatchCount)
{
    /// <summary>A query is typed: the header shows the match count and does not collapse.</summary>
    public bool IsSearching { get; init; }

    public bool IsOpen => !IsCollapsed;
}

/// <summary>
/// The action dialog (spec docs/design/controls-page.md §6.1, §6.3, §6.4): picks and sets up one
/// gesture's action of a button in the edited mode. The settings pane edits a copy; Done validates
/// it and writes it to the mode it was opened for, Remove empties the slot, and Cancel, Escape or a
/// click outside close without writing.
/// </summary>
public sealed partial class ActionDialogViewModel : ObservableObject, ISearchList, IDisposable
{
    private sealed record Entry(ButtonActionType Type, string Name, string Keywords);

    private static readonly (string Section, Entry[] Items)[] Catalog =
    {
        ("Sound", new Entry[]
        {
            new(ButtonActionType.MuteCurrentOutput, "Mute main output", "speakers sound silence volume"),
            new(ButtonActionType.MuteCurrentInput, "Mute microphone", "mic microphone silence"),
            new(ButtonActionType.MuteSpecificDevice, "Mute a device", "mic microphone silence headset"),
            new(ButtonActionType.SetOutputDevice, "Switch output", "device speakers headphones default"),
            new(ButtonActionType.ToggleOutputDevice, "Toggle outputs", "swap switch speakers headphones"),
        }),
        ("Media", new Entry[]
        {
            new(ButtonActionType.MediaPlayPause, "Play / pause", "music media spotify pause resume"),
            new(ButtonActionType.MediaNext, "Next track", "skip forward song"),
            new(ButtonActionType.MediaPrevious, "Previous track", "back previous song"),
            new(ButtonActionType.MediaStop, "Stop", "media"),
        }),
        ("Modes", new Entry[]
        {
            new(ButtonActionType.NextPreset, "Next mode", "preset cycle layer"),
            new(ButtonActionType.GoToPreset, "Go to mode", "preset layer"),
        }),
        ("Programs", new Entry[]
        {
            new(ButtonActionType.OpenProgram, "Open a program", "launch app exe start steam"),
            new(ButtonActionType.RunScript, "Run a script", "bat ps1 python run command"),
        }),
    };

    private readonly ButtonItemViewModel _button;
    private readonly ButtonsViewModel _buttons;
    private readonly Dictionary<string, bool> _collapsed;
    private readonly Action _close;
    private readonly string _presetId;
    private readonly ButtonActionConfig _original;
    private ButtonActionType? _current;

    /// <summary>"Press", "Double press" or "Hold".</summary>
    public string Title { get; }

    /// <summary>"Button {N} · {mode}".</summary>
    public string Subtitle { get; }

    public PressType Gesture { get; }

    public ButtonItemViewModel Button => _button;

    /// <summary>The settings pane, over a copy of the action.</summary>
    public ActionSlotViewModel Draft { get; }

    /// <summary>The slot has an action, so Remove is offered.</summary>
    public bool CanRemove { get; }

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoMatches), nameof(NoMatchesText))]
    private IReadOnlyList<ActionSection> _sections = Array.Empty<ActionSection>();

    /// <summary>The items shown, in list order (the open sections' items).</summary>
    public IReadOnlyList<ActionItem> VisibleItems => Sections.Where(s => !s.IsCollapsed).SelectMany(s => s.Items).ToList();

    public bool HasNoMatches => Sections.Count == 0;

    public string NoMatchesText => $"No actions match “{Query.Trim()}”.";

    /// <summary>An action is picked: the pane shows its settings and Done is enabled.</summary>
    public bool HasPick => Draft.Config.Action != ButtonActionType.None;

    /// <summary>The picked action's name, over its settings.</summary>
    public string PickedName => NameOf(Draft.Config.Action) ?? "";

    /// <summary>The picked action's <see cref="ButtonActionType"/> name, for its icon.</summary>
    public string PickedIconKey => Draft.Config.Action.ToString();

    internal ActionDialogViewModel(ButtonItemViewModel button, PressType gesture, ButtonsViewModel buttons,
                                   Dictionary<string, bool> collapsed, Action close)
    {
        _button = button;
        _buttons = buttons;
        _collapsed = collapsed;
        _close = close;
        Gesture = gesture;
        _presetId = buttons.EditingPresetId;
        Title = ButtonItemViewModel.GestureName(gesture);
        Subtitle = $"Button {button.Index + 1} · {buttons.EditingName}";
        _original = Clone(Of(button.Config.BindingFor(_presetId), gesture));
        CanRemove = _original.Action != ButtonActionType.None;
        Draft = button.CreateDraft(gesture, Clone(_original));
        _current = CanRemove ? _original.Action : null;
        _buttons.DevicesRefreshed += OnDevicesRefreshed;
        Rebuild();
    }

    private static ButtonActionConfig Of(ButtonBindingConfig binding, PressType gesture) => gesture switch
    {
        PressType.Double => binding.DoublePress,
        PressType.Long => binding.LongPress,
        _ => binding.SinglePress,
    };

    private static ButtonActionConfig Clone(ButtonActionConfig action)
        => JsonSerializer.Deserialize(JsonSerializer.Serialize(action, ConfigJsonContext.Default.ButtonActionConfig),
                                      ConfigJsonContext.Default.ButtonActionConfig)!;

    private static bool Same(ButtonActionConfig a, ButtonActionConfig b)
        => JsonSerializer.Serialize(a, ConfigJsonContext.Default.ButtonActionConfig)
           == JsonSerializer.Serialize(b, ConfigJsonContext.Default.ButtonActionConfig);

    private static string? NameOf(ButtonActionType type)
        => Catalog.SelectMany(s => s.Items).FirstOrDefault(e => e.Type == type)?.Name;

    private bool IsAvailable(ButtonActionType type) => ActionCatalog.For(type, Draft.Choices).IsAvailable;

    private static string Key(string section) => $"action:{section}";

    /// <summary>The mode and the button still exist (else the dialog has nothing to write to).</summary>
    internal bool IsStillValid(AppConfig config)
        => config.Presets.Exists(p => p.Id == _presetId) && _buttons.Items.Contains(_button);

    // ── The list ──────────────────────────────────────────────────────────────

    partial void OnQueryChanged(string value)
    {
        _current = null;
        Rebuild();
        // The top result is where Enter goes.
        if (Query.Trim().Length > 0) _current = VisibleItems.FirstOrDefault(i => i.IsEnabled)?.Type;
        Rebuild();
    }

    private void Rebuild()
    {
        var query = Query.Trim();
        var searching = query.Length > 0;
        var sections = new List<ActionSection>();
        foreach (var (name, entries) in Catalog)
        {
            var matches = new List<(Entry Entry, FuzzyMatch Match)>();
            foreach (var entry in entries)
            {
                if (!searching) matches.Add((entry, new FuzzyMatch(0, Array.Empty<int>())));
                else if (FuzzyMatcher.MatchItem(query, entry.Name, entry.Keywords) is { } match) matches.Add((entry, match));
            }
            if (matches.Count == 0) continue;
            if (searching) matches = matches.OrderByDescending(m => m.Match.Score).ToList();   // stable
            var items = matches.Select(m => new ActionItem(m.Entry.Type, m.Entry.Name, m.Match.Indexes, IsAvailable(m.Entry.Type))
            {
                IsPicked = HasPick && m.Entry.Type == Draft.Config.Action,
                IsCurrent = m.Entry.Type == _current,
            }).ToList();
            var collapsed = !searching && _collapsed.GetValueOrDefault(Key(name));
            sections.Add(new ActionSection(name, collapsed, items, items.Count) { IsSearching = searching });
        }
        Sections = sections;
        OnPropertyChanged(nameof(VisibleItems));
    }

    /// <summary>Opens or closes a section and remembers it for this app run. While searching every
    /// section with matches stays open, and the remembered state is left alone.</summary>
    [RelayCommand]
    private void ToggleSection(string name)
    {
        if (Query.Trim().Length > 0) return;
        _collapsed[Key(name)] = !_collapsed.GetValueOrDefault(Key(name));
        Rebuild();
    }

    /// <summary>Down (+1) and Up (−1) through the shown, enabled items.</summary>
    public void MoveSelection(int step)
    {
        var items = VisibleItems.Where(i => i.IsEnabled).ToList();
        if (items.Count == 0) return;
        var at = items.FindIndex(i => i.Type == _current);
        var next = at < 0 ? (step > 0 ? 0 : items.Count - 1) : Math.Clamp(at + step, 0, items.Count - 1);
        _current = items[next].Type;
        Rebuild();
    }

    /// <summary>Enter: picks the current item (the top result after typing).</summary>
    public void PickCurrent()
    {
        var item = VisibleItems.FirstOrDefault(i => i.Type == _current && i.IsEnabled)
                   ?? VisibleItems.FirstOrDefault(i => i.IsEnabled);
        if (item is not null) Pick(item.Type);
    }

    /// <summary>Picks an action. A different one starts empty (no device, mode or file; the user's
    /// choice is never guessed) and clears the errors shown.</summary>
    [RelayCommand]
    public void Pick(ButtonActionType t)
    {
        if (t == ButtonActionType.None || !IsAvailable(t)) return;
        _current = t;
        if (t != Draft.Config.Action)
        {
            Draft.ShowErrors = false;
            Draft.Bind(new ButtonActionConfig { Action = t });
            OnPropertyChanged(nameof(HasPick));
            OnPropertyChanged(nameof(PickedName));
            OnPropertyChanged(nameof(PickedIconKey));
        }
        Rebuild();
    }

    // ── Footer ────────────────────────────────────────────────────────────────

    /// <summary>Validates the draft and writes it. False, with the errors shown under their fields,
    /// when it is invalid (or nothing is picked). An unchanged action just closes.</summary>
    public bool Done()
    {
        if (!HasPick) return false;
        if (!IsStillValid(((IButtonCardOwner)_buttons).AppConfig)) { _close(); return false; }
        if (Same(Draft.Config, _original)) { _close(); return true; }
        Draft.ShowErrors = true;
        if (Draft.Check().Count > 0) return false;
        _button.SetAction(_presetId, Gesture, Clone(Draft.Config));
        _close();
        return true;
    }

    /// <summary>Empties the slot.</summary>
    [RelayCommand]
    public void Remove()
    {
        if (IsStillValid(((IButtonCardOwner)_buttons).AppConfig)) _button.SetAction(_presetId, Gesture, new ButtonActionConfig());
        _close();
    }

    [RelayCommand]
    public void Cancel() => _close();

    private void OnDevicesRefreshed(IReadOnlyList<AudioDevice> devices) => Draft.RefreshDevices(devices);

    public void Dispose() => _buttons.DevicesRefreshed -= OnDevicesRefreshed;
}
