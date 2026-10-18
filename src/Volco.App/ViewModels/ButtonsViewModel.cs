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
/// platform runs, their live state for the previews, and the media sessions for the app picker.</summary>
public sealed record ActionCapabilities(ActionAvailability Availability, IActionStatus Status, IMediaSessions? Media)
{
    public static ActionCapabilities None { get; } = new(new ActionAvailability(false, false, false), ActionStatus.None, null);

    public static ActionCapabilities Of(VolcoRuntime runtime)
        => new(new ActionAvailability(runtime.Media is not null, runtime.ProgramsAvailable, runtime.ScriptsAvailable),
               runtime.ActionStatus, runtime.Media);
}

/// <summary>
/// The buttons of the Controls page: presets (only button actions change per preset) and the
/// buttons in hardware-input order. Position is identity, as for sliders; moving a button moves
/// its actions in every preset.
/// </summary>
public sealed partial class ButtonsViewModel : ObservableObject, IButtonCardOwner
{
    private readonly ConfigManager _config;
    private readonly IAudioEngine _audio;
    private readonly IPresetController _presets;
    private readonly Action<ButtonActionConfig, ActionOrigin?> _execute;
    private readonly UndoToastViewModel _toast;
    private readonly ActionCapabilities _capabilities;
    private readonly ActionSlotHost _slotHost;
    private IReadOnlyList<AudioDevice> _devices;
    private IReadOnlyList<AudioDevice> _captureDevices;

    public ObservableCollection<ButtonItemViewModel> Items { get; } = new();
    public ObservableCollection<PresetTabViewModel> Presets { get; } = new();

    /// <summary>The preset whose actions the buttons show and edit.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingLive), nameof(EditingName), nameof(MakeLiveText),
                              nameof(EditingTintIndex))]
    private string _editingPresetId;

    /// <summary>The preset the controller is using (from the runtime).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingLive))]
    private string _livePresetId;

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

    public bool IsEditingLive => EditingPresetId == LivePresetId;
    public string EditingName => PresetName(EditingPresetId) ?? "";

    /// <summary>The edited preset's colour (its tab's), which frames the buttons.</summary>
    public int EditingTintIndex => Math.Max(0, _config.Config.Presets.FindIndex(p => p.Id == EditingPresetId));

    public string MakeLiveText => $"Make {EditingName} live";

    public ButtonsViewModel(ConfigManager config, IAudioEngine audio, IPresetController presets,
                            Action<ButtonActionConfig, ActionOrigin?> execute, UndoToastViewModel toast,
                            ActionCapabilities? capabilities = null, Func<string, bool>? fileExists = null)
    {
        _capabilities = capabilities ?? ActionCapabilities.None;
        _slotHost = new ActionSlotHost(_capabilities.Availability,
                                       () => _capabilities.Media?.GetApps() ?? Array.Empty<MediaApp>(),
                                       fileExists ?? File.Exists);
        _config = config;
        _audio = audio;
        _presets = presets;
        _execute = execute;
        _toast = toast;
        _devices = audio.GetDevices(AudioFlow.Render);
        _captureDevices = audio.GetDevices(AudioFlow.Capture);
        _livePresetId = presets.ActivePresetId;
        _editingPresetId = _livePresetId;
        RebuildPresetTabs();
        for (int i = 0; i < Buttons.Count; i++)
            Items.Add(new ButtonItemViewModel(Buttons[i], i, _devices, this));
        Renumber();
        RefreshController();
    }

    /// <summary>Re-reads the controller's hardware (a Hello arrived): the lights follow.</summary>
    public void RefreshController() => RefreshLights();

    private List<ButtonConfig> Buttons => _config.Config.Buttons;
    private void Save() => _config.RequestSave();

    // ── IButtonCardOwner ──────────────────────────────────────────────────────

    string IButtonCardOwner.EditingPresetId => EditingPresetId;
    string IButtonCardOwner.LivePresetId => LivePresetId;
    AppConfig IButtonCardOwner.AppConfig => _config.Config;
    IAudioEngine IButtonCardOwner.Audio => _audio;

    public IReadOnlyList<PresetChoice> PresetChoices()
        => _config.Config.Presets.Select(p => new PresetChoice(p.Id, p.Name)).ToList();

    public string? PresetName(string? id) => _config.Config.Presets.Find(p => p.Id == id)?.Name;

    public IndicatorKind? IndicatorFor(int index)
        => _config.Config.Controller is not { } c ? null
           : index < c.Buttons.Count ? c.Buttons[index] : IndicatorKind.None;

    public DeviceChoice Describe(string friendlyName) => DeviceChoice.For(_config.Config, _devices, friendlyName);

    public string DeviceName(DeviceRefConfig? device) => DeviceDisplay.NameOf(_config.Config, _audio, device, "?");

    ActionValidationContext IButtonCardOwner.ValidationContext()
        => new(_config.Config.Presets.Select(p => p.Id).ToList(), DeviceFlow, _slotHost.FileExists);

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

    void IButtonCardOwner.Changed(ButtonItemViewModel item) => Save();

    // ── Presets ───────────────────────────────────────────────────────────────

    private void RebuildPresetTabs()
    {
        Presets.Clear();
        var presets = _config.Config.Presets;
        for (int i = 0; i < presets.Count; i++)
            Presets.Add(new PresetTabViewModel(presets[i].Id, presets[i].Name, i, tab => EditingPresetId = tab.Id, DeleteMode));
        // A delete, undo or move shifts the presets after it, and their colours with them.
        OnPropertyChanged(nameof(EditingTintIndex));
        UpdateTabFlags();
    }

    private void UpdateTabFlags()
    {
        foreach (var tab in Presets)
        {
            tab.IsEditing = tab.Id == EditingPresetId;
            tab.IsLive = tab.Id == LivePresetId;
            tab.IsRenaming = tab.Id == RenamingModeId;
            tab.IsEditingModes = IsEditingModes;
        }
    }

    partial void OnEditingPresetIdChanged(string value)
    {
        UpdateTabFlags();
        foreach (var item in Items) item.Reload();
    }

    partial void OnLivePresetIdChanged(string value)
    {
        UpdateTabFlags();
        RefreshLights();
    }

    partial void OnRenamingModeIdChanged(string? value) => UpdateTabFlags();

    partial void OnIsEditingModesChanged(bool value) => UpdateTabFlags();

    /// <summary>Called (on the UI thread) when the runtime's live preset changed.</summary>
    public void OnRuntimePresetChanged(string presetId) => LivePresetId = presetId;

    /// <summary>Adds an empty mode at the end and edits it; the live mode stays. Its menu opens.</summary>
    [RelayCommand]
    private void AddMode()
    {
        var presets = _config.Config.Presets;
        var id = NewPresetId();
        int n = 2;
        while (presets.Any(p => p.Name == $"Mode {n}")) n++;
        presets.Add(new PresetConfig { Id = id, Name = $"Mode {n}" });
        RebuildPresetTabs();
        RefreshPresetPickers();
        EditingPresetId = id;
        MenuModeId = id;
        Save();
    }

    private string NewPresetId()
    {
        var existing = new HashSet<string>(_config.Config.Presets.Select(p => p.Id));
        for (int n = 2; ; n++)
            if (!existing.Contains($"preset-{n}")) return $"preset-{n}";
    }

    public void OpenMenu(string modeId) => MenuModeId = modeId;

    public void RenameMode(string modeId, string name)
    {
        name = name.Trim();
        var preset = _config.Config.Presets.Find(p => p.Id == modeId);
        if (preset is null || name.Length == 0 || name == preset.Name) return;
        preset.Name = name;
        var tab = Presets.FirstOrDefault(t => t.Id == preset.Id);
        if (tab is not null) tab.Name = name;
        OnPropertyChanged(nameof(EditingName));
        OnPropertyChanged(nameof(MakeLiveText));
        RefreshPresetPickers();
        Save();
    }

    /// <summary>Replaces <paramref name="toId"/>'s actions on every button with copies of
    /// <paramref name="fromId"/>'s.</summary>
    public void CopyActions(string fromId, string toId)
    {
        var from = PresetName(fromId);
        var to = PresetName(toId);
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
        RefreshPresetPickers();
    }

    /// <summary>Deletes a mode (not the first, and not the last one left).</summary>
    public void DeleteMode(string modeId)
    {
        var presets = _config.Config.Presets;
        var index = presets.FindIndex(p => p.Id == modeId);
        if (index <= 0 || presets.Count < 2) return;
        var preset = presets[index];
        var removedActions = new Dictionary<ButtonConfig, ButtonBindingConfig>();
        foreach (var button in Buttons)
            if (button.Actions.Remove(preset.Id, out var binding) && binding is not null)
                removedActions[button] = binding;
        presets.RemoveAt(index);
        var wasLive = LivePresetId == preset.Id;
        var wasEditing = EditingPresetId == preset.Id;
        if (MenuModeId == preset.Id) MenuModeId = null;
        if (RenamingModeId == preset.Id) RenamingModeId = null;

        RebuildPresetTabs();
        RefreshPresetPickers();
        // Edit the tab before the deleted one when it was the edited one.
        if (wasEditing) EditingPresetId = presets[Math.Max(0, index - 1)].Id;
        // The runtime already falls back to the first preset once the live one is gone (and so
        // raises no change); read it back rather than wait for an event.
        if (wasLive) _presets.SetActivePreset(presets[0].Id);
        LivePresetId = _presets.ActivePresetId;
        Save();

        _toast.Show($"Mode “{preset.Name}” deleted", () =>
        {
            presets.Insert(Math.Min(index, presets.Count), preset);
            foreach (var (button, binding) in removedActions)
                button.Actions[preset.Id] = binding;
            RebuildPresetTabs();
            RefreshPresetPickers();
            if (wasEditing) EditingPresetId = preset.Id;
            if (wasLive) _presets.SetActivePreset(preset.Id);
            LivePresetId = _presets.ActivePresetId;
            Save();
        });
    }

    /// <summary>Moves a mode to another position (the first is the start-up mode); colours follow positions.</summary>
    public void MoveMode(int from, int to)
    {
        var presets = _config.Config.Presets;
        if (from == to || from < 0 || to < 0 || from >= presets.Count || to >= presets.Count) return;
        var preset = presets[from];
        presets.RemoveAt(from);
        presets.Insert(to, preset);
        RebuildPresetTabs();
        RefreshPresetPickers();
        Save();
    }

    /// <summary>Makes the edited mode the one the controller uses.</summary>
    [RelayCommand]
    private void MakeLive() => MakeLive(EditingPresetId);

    public void MakeLive(string modeId) => _presets.SetActivePreset(modeId);

    [RelayCommand]
    private void ToggleEditModes()
    {
        IsEditingModes = !IsEditingModes;
        if (!IsEditingModes) RenamingModeId = null;
    }

    public void StartRename(string modeId)
    {
        RenameText = PresetName(modeId) ?? "";
        RenamingModeId = modeId;
    }

    public void CommitRename()
    {
        if (RenamingModeId is { } id) RenameMode(id, RenameText);
        RenamingModeId = null;
    }

    public void CancelRename() => RenamingModeId = null;

    private void RefreshPresetPickers()
    {
        foreach (var item in Items)
        {
            foreach (var slot in item.Slots) slot.RefreshPresets();
            item.Refresh();
            item.RefreshProblems();   // Go to preset checks that its preset exists
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

    public void Move(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= Items.Count || to >= Items.Count) { Renumber(); return; }
        var cfg = Buttons[from];
        Buttons.RemoveAt(from);
        Buttons.Insert(to, cfg);
        Items.Move(from, to);
        Renumber();
        Save();
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

    /// <summary>Recomputes every light preview (audio state or the live preset changed).</summary>
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
