using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Devices;
using Volco.Core.Input;
using Volco.Core.Input.Actions;
using Volco.Core.Media;
using Volco.Core.Runtime;

namespace Volco.App.ViewModels;

/// <summary>What the Controls page knows about media, program and script actions: which this
/// platform runs, their live state for the previews, and the media sessions for the app picker.
/// <paramref name="Feedback"/> is what a button shows for a moment after a press or a mode change
/// (null: nothing), so the previews show it as the controller does.</summary>
public sealed record ActionCapabilities(ActionAvailability Availability, IActionStatus Status, IMediaSessions? Media,
                                        IButtonFeedback? Feedback = null)
{
    public static ActionCapabilities None { get; } = new(new ActionAvailability(false, false, false), ActionStatus.None, null);

    public static ActionCapabilities Of(VolcoRuntime runtime)
        => new(new ActionAvailability(runtime.Media is not null, runtime.ProgramsAvailable, runtime.ScriptsAvailable),
               runtime.ActionStatus, runtime.Media, runtime);
}

/// <summary>
/// The buttons of the Controls page: modes (only button actions change per mode) and the
/// buttons in hardware-input order. Position is identity, as for sliders; moving a button moves
/// its actions in every mode.
/// </summary>
public sealed partial class ButtonsViewModel : ObservableObject, IButtonCardOwner
{
    private readonly ConfigManager _config;
    private readonly IAudioEngine _audio;
    private readonly IModeController _modes;
    private readonly Action<ButtonActionConfig, ActionOrigin?> _execute;
    private readonly UndoToastViewModel _toast;
    private readonly ActionCapabilities _capabilities;
    private readonly ActionSlotHost _slotHost;
    private IReadOnlyList<AudioDevice> _devices;
    private IReadOnlyList<AudioDevice> _captureDevices;

    public ObservableCollection<ButtonItemViewModel> Items { get; } = new();
    public ObservableCollection<ModeTabViewModel> Modes { get; } = new();

    /// <summary>The mode whose actions the buttons show and edit.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingLive), nameof(EditingName), nameof(MakeLiveText),
                              nameof(EditingTintIndex))]
    private string _editingModeId;

    /// <summary>The mode the controller is using (from the runtime).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingLive))]
    private string _liveModeId;

    /// <summary>The mode whose menu is open (null = closed).</summary>
    [ObservableProperty]
    private string? _menuModeId;

    /// <summary>Edit mode of the tab strip: the tabs can be renamed, moved and deleted.</summary>
    [ObservableProperty]
    private bool _isEditingModes;

    /// <summary>The tab being renamed inline.</summary>
    [ObservableProperty]
    private string? _renamingModeId;

    [ObservableProperty]
    private string _renameText = "";

    public bool IsEditingLive => EditingModeId == LiveModeId;
    public string EditingName => ModeName(EditingModeId) ?? "";

    /// <summary>The edited mode's colour (its tab's), which frames the buttons.</summary>
    public int EditingTintIndex => Math.Max(0, _config.Config.Modes.FindIndex(p => p.Id == EditingModeId));

    public string MakeLiveText => $"Make {EditingName} live";

    public ButtonsViewModel(ConfigManager config, IAudioEngine audio, IModeController modes,
                            Action<ButtonActionConfig, ActionOrigin?> execute, UndoToastViewModel toast,
                            ActionCapabilities? capabilities = null, Func<string, bool>? fileExists = null)
    {
        _capabilities = capabilities ?? ActionCapabilities.None;
        _slotHost = new ActionSlotHost(_capabilities.Availability,
                                       () => _capabilities.Media?.GetApps() ?? Array.Empty<MediaApp>(),
                                       fileExists ?? File.Exists);
        _config = config;
        _audio = audio;
        _modes = modes;
        _execute = execute;
        _toast = toast;
        _devices = audio.GetDevices(AudioFlow.Render);
        _captureDevices = audio.GetDevices(AudioFlow.Capture);
        _liveModeId = modes.ActiveModeId;
        _editingModeId = _liveModeId;
        RebuildModeTabs();
        for (int i = 0; i < Buttons.Count; i++)
            Items.Add(new ButtonItemViewModel(Buttons[i], i, _devices, this));
        Renumber();
        RefreshController();
    }

    /// <summary>Re-reads the controller's hardware (a Hello arrived): the lights follow.</summary>
    public void RefreshController() => RefreshLights();

    private List<ButtonConfig> Buttons => _config.Config.Buttons;
    // Ids deleted while this view model lives: a delete's Undo may still be on offer.
    private readonly HashSet<string> _deletedModeIds = new();
    private void Save() => _config.RequestSave();

    // ── IButtonCardOwner ──────────────────────────────────────────────────────

    string IButtonCardOwner.EditingModeId => EditingModeId;
    string IButtonCardOwner.LiveModeId => LiveModeId;
    AppConfig IButtonCardOwner.AppConfig => _config.Config;
    IAudioEngine IButtonCardOwner.Audio => _audio;

    public IReadOnlyList<ModeChoice> ModeChoices()
        => _config.Config.Modes.Select(p => new ModeChoice(p.Id, p.Name)).ToList();

    public string? ModeName(string? id) => _config.Config.Modes.Find(p => p.Id == id)?.Name;

    public IndicatorKind? IndicatorFor(int index)
        => _config.Config.Controller is not { } c ? null
           : index < c.Buttons.Count ? c.Buttons[index] : IndicatorKind.None;

    public DeviceChoice Describe(string friendlyName) => DeviceChoice.For(_config.Config, _devices, friendlyName);

    public string DeviceName(DeviceRefConfig? device) => DeviceDisplay.NameOf(_config.Config, _audio, device, "?");

    ActionValidationContext IButtonCardOwner.ValidationContext()
        => new(_config.Config.Modes.Select(p => p.Id).ToList(), DeviceFlow, _slotHost.FileExists);

    /// <summary>The flow of a plugged-in device (by id, else by name); null when it isn't plugged in.</summary>
    private AudioFlow? DeviceFlow(DeviceRefConfig device)
    {
        var all = _devices.Concat(_captureDevices);
        var match = device.Id.Length > 0
            ? all.FirstOrDefault(d => d.Id == device.Id)
            : all.FirstOrDefault(d => string.Equals(d.FriendlyName, device.FriendlyName, StringComparison.OrdinalIgnoreCase));
        return match?.Flow;
    }

    void IButtonCardOwner.Execute(ButtonActionConfig action, int button, PressType gesture)
        => _execute(action, new ActionOrigin(button, gesture, EditingName));

    ActionSlotHost IButtonCardOwner.SlotHost => _slotHost;
    IActionStatus IButtonCardOwner.ActionStatus => _capabilities.Status;

    IButtonFeedback? IButtonCardOwner.Feedback => _capabilities.Feedback;

    void IButtonCardOwner.Changed(ButtonItemViewModel item) => Save();

    // ── Modes ───────────────────────────────────────────────────────────────

    private void RebuildModeTabs()
    {
        Modes.Clear();
        var modes = _config.Config.Modes;
        for (int i = 0; i < modes.Count; i++)
            Modes.Add(new ModeTabViewModel(modes[i].Id, modes[i].Name, i, tab => EditingModeId = tab.Id, DeleteMode));
        // A delete, undo or move shifts the modes after it, and their colours with them.
        OnPropertyChanged(nameof(EditingTintIndex));
        UpdateTabFlags();
    }

    private void UpdateTabFlags()
    {
        foreach (var tab in Modes)
        {
            tab.IsEditing = tab.Id == EditingModeId;
            tab.IsLive = tab.Id == LiveModeId;
            tab.IsRenaming = tab.Id == RenamingModeId;
            tab.IsEditingModes = IsEditingModes;
        }
    }

    partial void OnEditingModeIdChanged(string value)
    {
        UpdateTabFlags();
        foreach (var item in Items) item.Reload();
    }

    partial void OnLiveModeIdChanged(string value)
    {
        UpdateTabFlags();
        RefreshLights();
    }

    partial void OnRenamingModeIdChanged(string? value) => UpdateTabFlags();

    partial void OnIsEditingModesChanged(bool value) => UpdateTabFlags();

    /// <summary>Called (on the UI thread) when the runtime's live mode changed.</summary>
    public void OnRuntimeModeChanged(string modeId) => LiveModeId = modeId;

    /// <summary>Adds an empty mode at the end and edits it; the live mode stays. Its menu opens.</summary>
    [RelayCommand]
    private void AddMode()
    {
        var modes = _config.Config.Modes;
        var id = NewModeId();
        int n = 2;
        while (modes.Any(p => p.Name == $"Mode {n}")) n++;
        modes.Add(new ModeConfig { Id = id, Name = $"Mode {n}" });
        RebuildModeTabs();
        RefreshModePickers();
        EditingModeId = id;
        MenuModeId = id;
        Save();
    }

    /// <summary>A mode id never used before: not a mode's, not a key of any button's actions, not
    /// any action's Go to mode target (a dangling one must not re-point to the new mode), and not
    /// one deleted while this page lives.</summary>
    private string NewModeId()
    {
        var existing = new HashSet<string>(_config.Config.Modes.Select(p => p.Id));
        existing.UnionWith(_deletedModeIds);
        foreach (var button in Buttons)
            foreach (var (key, binding) in button.Actions)
            {
                existing.Add(key);
                if (binding is null) continue;
                foreach (var action in binding.All())
                    if (action.ModeId is { } target) existing.Add(target);
            }
        for (int n = 2; ; n++)
            if (!existing.Contains($"mode-{n}")) return $"mode-{n}";
    }

    public void OpenMenu(string modeId) => MenuModeId = modeId;

    public void RenameMode(string modeId, string name)
    {
        name = name.Trim();
        var mode = _config.Config.Modes.Find(p => p.Id == modeId);
        if (mode is null || name.Length == 0 || name == mode.Name) return;
        mode.Name = name;
        var tab = Modes.FirstOrDefault(t => t.Id == mode.Id);
        if (tab is not null) tab.Name = name;
        OnPropertyChanged(nameof(EditingName));
        OnPropertyChanged(nameof(MakeLiveText));
        RefreshModePickers();
        Save();
    }

    /// <summary>Replaces <paramref name="toId"/>'s actions on every button with copies of
    /// <paramref name="fromId"/>'s.</summary>
    public void CopyActions(string fromId, string toId)
    {
        var from = ModeName(fromId);
        var to = ModeName(toId);
        if (from is null || to is null || fromId == toId) return;
        var previous = new Dictionary<ButtonConfig, ButtonBindingConfig?>();
        foreach (var button in Buttons)
        {
            previous[button] = button.Actions.TryGetValue(toId, out var old) ? old : null;
            if (button.Actions.TryGetValue(fromId, out var source) && source is not null)
                button.Actions[toId] = CloneJson(source, ConfigJsonContext.Default.ButtonBindingConfig);
            else
                button.Actions.Remove(toId);
        }
        ActionsReplaced();
        Save();

        _toast.Show($"Copied “{from}” into “{to}”", () =>
        {
            foreach (var (button, old) in previous)
            {
                if (old is null) button.Actions.Remove(toId);
                else button.Actions[toId] = old;
            }
            ActionsReplaced();
            Save();
        });
    }

    private void ActionsReplaced()
    {
        foreach (var item in Items) item.Reload();
        RefreshModePickers();
    }

    /// <summary>Deletes a mode (not the first, and not the last one left).</summary>
    public void DeleteMode(string modeId)
    {
        var modes = _config.Config.Modes;
        var index = modes.FindIndex(p => p.Id == modeId);
        if (index <= 0 || modes.Count < 2) return;
        var mode = modes[index];
        var removedActions = new Dictionary<ButtonConfig, ButtonBindingConfig>();
        foreach (var button in Buttons)
            if (button.Actions.Remove(mode.Id, out var binding) && binding is not null)
                removedActions[button] = binding;
        modes.RemoveAt(index);
        _deletedModeIds.Add(mode.Id);
        var wasLive = LiveModeId == mode.Id;
        var wasEditing = EditingModeId == mode.Id;
        if (MenuModeId == mode.Id) MenuModeId = null;
        if (RenamingModeId == mode.Id) RenamingModeId = null;

        RebuildModeTabs();
        RefreshModePickers();
        // Edit the tab before the deleted one when it was the edited one.
        if (wasEditing) EditingModeId = modes[Math.Max(0, index - 1)].Id;
        // The runtime already falls back to the first mode once the live one is gone (and so
        // raises no change); read it back rather than wait for an event.
        if (wasLive) _modes.SetActiveMode(modes[0].Id);
        LiveModeId = _modes.ActiveModeId;
        Save();

        _toast.Show($"Mode “{mode.Name}” deleted", () =>
        {
            // Never two modes with one id.
            if (modes.Any(p => p.Id == mode.Id)) return;
            modes.Insert(Math.Min(index, modes.Count), mode);
            foreach (var (button, binding) in removedActions)
                button.Actions[mode.Id] = binding;
            RebuildModeTabs();
            RefreshModePickers();
            if (wasEditing) EditingModeId = mode.Id;
            if (wasLive) _modes.SetActiveMode(mode.Id);
            LiveModeId = _modes.ActiveModeId;
            Save();
        });
    }

    /// <summary>Moves a mode to another position (the first is the start-up mode); colours follow positions.</summary>
    public void MoveMode(int from, int to)
    {
        var modes = _config.Config.Modes;
        if (from == to || from < 0 || to < 0 || from >= modes.Count || to >= modes.Count) return;
        var mode = modes[from];
        modes.RemoveAt(from);
        modes.Insert(to, mode);
        RebuildModeTabs();
        RefreshModePickers();
        Save();
    }

    /// <summary>Makes the edited mode the one the controller uses.</summary>
    [RelayCommand]
    private void MakeLive() => MakeLive(EditingModeId);

    public void MakeLive(string modeId) => _modes.SetActiveMode(modeId);

    [RelayCommand]
    private void ToggleEditModes()
    {
        IsEditingModes = !IsEditingModes;
        if (!IsEditingModes) RenamingModeId = null;
    }

    public void StartRename(string modeId)
    {
        RenameText = ModeName(modeId) ?? "";
        RenamingModeId = modeId;
    }

    public void CommitRename()
    {
        if (RenamingModeId is { } id) RenameMode(id, RenameText);
        RenamingModeId = null;
    }

    public void CancelRename() => RenamingModeId = null;

    private void RefreshModePickers()
    {
        foreach (var item in Items)
        {
            foreach (var slot in item.Slots) slot.RefreshModes();
            item.Refresh();
            item.RefreshProblems();   // Go to mode checks that its mode exists
        }
    }

    // ── Add / remove ──────────────────────────────────────────────────────────

    /// <summary>Adds an empty button for each input the controller has beyond the list.</summary>
    [RelayCommand]
    public void AddMissing()
    {
        var target = _config.Config.Controller?.Buttons.Count ?? 0;
        if (Items.Count >= target) return;
        while (Items.Count < target)
        {
            var cfg = new ButtonConfig();
            Buttons.Add(cfg);
            Items.Add(new ButtonItemViewModel(cfg, Items.Count, _devices, this));
        }
        Renumber();
        RefreshController();
        Save();
    }

    /// <summary>An empty button for a control the Hardware page's draft adds: not in
    /// <see cref="Items"/> and never saved (Volco's settings get it once the controller confirms).</summary>
    public ButtonItemViewModel CreateDetached(int index)
        => new(new ButtonConfig { Index = index }, index, _devices, this) { IsNew = true };

    /// <summary>Rebuilds <see cref="Items"/> from <c>config.json</c> after its list was rewritten
    /// (a confirmed save removed or added controls).</summary>
    public void Reload()
    {
        Items.Clear();
        for (int i = 0; i < Buttons.Count; i++)
            Items.Add(new ButtonItemViewModel(Buttons[i], i, _devices, this));
        Renumber();
        RefreshController();
    }

    public void RemoveLast()
    {
        if (Items.Count == 0) return;
        var item = Items[^1];
        var cfg = Buttons[^1];
        Items.RemoveAt(Items.Count - 1);
        Buttons.RemoveAt(Buttons.Count - 1);
        Renumber();
        RefreshController();
        Save();
        _toast.Show($"{item.Heading} removed", () =>
        {
            Buttons.Add(cfg);
            Items.Add(item);
            Renumber();
            RefreshController();
            Save();
        });
    }

    // ── Reordering ────────────────────────────────────────────────────────────

    /// <summary>Swaps the edited mode's actions between buttons <paramref name="a"/> and
    /// <paramref name="b"/>; other modes and the timing stay with their positions. A mode a button
    /// has no entry for moves as "missing".</summary>
    public void SwapActions(int a, int b)
    {
        if (a == b || a < 0 || b < 0 || a >= Items.Count || b >= Items.Count) return;
        var id = EditingModeId;
        DoSwap(a, b, id);
        Save();
        _toast.Show($"Swapped button {a + 1} and button {b + 1} in {EditingName}", () =>
        {
            DoSwap(a, b, id);
            Save();
        });
    }

    private void DoSwap(int a, int b, string id)
    {
        var first = Items[a].Config.Actions;
        var second = Items[b].Config.Actions;
        first.TryGetValue(id, out var fromA);
        second.TryGetValue(id, out var fromB);
        if (fromB is null) first.Remove(id); else first[id] = fromB;
        if (fromA is null) second.Remove(id); else second[id] = fromA;
        Items[a].Reload();
        Items[b].Reload();
    }

    private void Renumber()
    {
        for (int i = 0; i < Items.Count; i++)
        {
            Items[i].Index = i;
            Items[i].IsLast = i == Items.Count - 1;
        }
    }

    // ── Live updates (UI thread) ──────────────────────────────────────────────

    /// <summary>Flashes the tile of the button at hardware input <paramref name="index"/>.</summary>
    public void Pulse(int index)
    {
        if (index >= 0 && index < Items.Count) Items[index].Pulse();
    }

    /// <summary>Recomputes the light and screen preview of button <paramref name="index"/> (-1: of
    /// every button): its press feedback or a mode change's announce moved on.</summary>
    public void RefreshFeedback(int index)
    {
        if (index < 0) RefreshLights();
        else if (index < Items.Count) Items[index].Refresh();
    }

    /// <summary>Recomputes every light preview (audio state or the live mode changed).</summary>
    public void RefreshLights()
    {
        foreach (var item in Items) item.Refresh();
    }

    /// <summary>A media session, a watched program or a script run changed.</summary>
    public void RefreshActionState()
    {
        foreach (var item in Items) item.RefreshActionState();
    }

    public void RefreshDevices(IReadOnlyList<AudioDevice> devices)
    {
        _devices = devices;
        _captureDevices = _audio.GetDevices(AudioFlow.Capture);
        foreach (var item in Items) item.RefreshDevices(devices);
        RefreshLights();
        foreach (var item in Items) item.RefreshProblems();   // an output may now be an input, or gone
        DevicesRefreshed?.Invoke(devices);
    }

    /// <summary>After <see cref="RefreshDevices"/>: an open action dialog's pickers follow.</summary>
    internal event Action<IReadOnlyList<AudioDevice>>? DevicesRefreshed;

    private static T CloneJson<T>(T value, JsonTypeInfo<T> type)
        => JsonSerializer.Deserialize(JsonSerializer.Serialize(value, type), type)!;
}
