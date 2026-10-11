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

/// <summary>What the Buttons page knows about media, program and script actions: which this
/// platform runs, their live state for the previews, and the media sessions for the app picker.</summary>
public sealed record ActionCapabilities(ActionAvailability Availability, IActionStatus Status, IMediaSessions? Media)
{
    public static ActionCapabilities None { get; } = new(new ActionAvailability(false, false, false), ActionStatus.None, null);

    public static ActionCapabilities Of(VolcoRuntime runtime)
        => new(new ActionAvailability(runtime.Media is not null, runtime.ProgramsAvailable, runtime.ScriptsAvailable),
               runtime.ActionStatus, runtime.Media);
}

/// <summary>
/// The Buttons page: presets (only button actions change per preset) and one card per configured
/// button in hardware-input order. Position is identity, as on the Sliders page; moving a card
/// moves its actions in every preset.
/// </summary>
public sealed partial class ButtonsViewModel : ObservableObject, IReorderableList, IButtonCardOwner
{
    private readonly ConfigManager _config;
    private readonly IAudioEngine _audio;
    private readonly IPresetController _presets;
    private readonly Action<ButtonActionConfig, ActionOrigin?> _execute;
    private readonly UndoToastViewModel _toast;
    private readonly ActionCapabilities _capabilities;
    private readonly ActionSlotHost _slotHost;
    private IReadOnlyList<AudioDevice> _devices;

    public ObservableCollection<ButtonItemViewModel> Items { get; } = new();
    public ObservableCollection<PresetTabViewModel> Presets { get; } = new();

    /// <summary>The preset whose actions the cards show and edit.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingLive), nameof(EditingName), nameof(BannerText), nameof(MakeLiveText))]
    private string _editingPresetId;

    /// <summary>The preset the controller is using (from the runtime).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingLive), nameof(LiveName), nameof(BannerText))]
    private string _livePresetId;

    [ObservableProperty]
    private bool _hasMultiplePresets;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _renameText = "";

    public bool IsEditingLive => EditingPresetId == LivePresetId;
    public string EditingName => PresetName(EditingPresetId) ?? "";
    public string LiveName => PresetName(LivePresetId) ?? "";

    /// <summary>The line under the preset tabs.</summary>
    public string BannerText => IsEditingLive
        ? $"{EditingName} is live on the controller."
        : $"You are editing {EditingName}. The controller is using {LiveName}.";

    public string MakeLiveText => $"Make {EditingName} live";

    public ButtonsViewModel(ConfigManager config, IAudioEngine audio, IPresetController presets,
                            Action<ButtonActionConfig, ActionOrigin?> execute, UndoToastViewModel toast,
                            ActionCapabilities? capabilities = null)
    {
        _capabilities = capabilities ?? ActionCapabilities.None;
        _slotHost = new ActionSlotHost(_capabilities.Availability,
                                       () => _capabilities.Media?.GetApps() ?? Array.Empty<MediaApp>(),
                                       () => config.Config.Connection.Type != ConnectionType.Serial,
                                       File.Exists);
        _config = config;
        _audio = audio;
        _presets = presets;
        _execute = execute;
        _toast = toast;
        _devices = audio.GetDevices(AudioFlow.Render);
        _livePresetId = presets.ActivePresetId;
        _editingPresetId = _livePresetId;
        RebuildPresetTabs();
        for (int i = 0; i < Buttons.Count; i++)
            Items.Add(new ButtonItemViewModel(Buttons[i], i, _devices, this));
        Renumber();
        RefreshController();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCountHint))]
    private string _countHint = "";

    public bool HasCountHint => CountHint.Length > 0;

    [ObservableProperty]
    private bool _canAddMissing;

    [ObservableProperty]
    private string _addMissingText = "";

    /// <summary>Re-reads the controller's hardware (a Hello arrived) or the card count changed.</summary>
    public void RefreshController()
    {
        var n = _config.Config.Controller?.Buttons.Count;
        CountHint = CountHints.Text("button", n, Items.Count);
        var missing = (n ?? 0) - Items.Count;
        CanAddMissing = missing > 0;
        AddMissingText = missing == 1 ? "Add the missing one" : $"Add the missing {missing}";
        RefreshLights();
    }

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

    void IButtonCardOwner.Execute(ButtonActionConfig action, int button, PressType gesture)
        => _execute(action, new ActionOrigin(button, gesture, EditingName));

    ActionSlotHost IButtonCardOwner.SlotHost => _slotHost;
    IActionStatus IButtonCardOwner.ActionStatus => _capabilities.Status;

    void IButtonCardOwner.Changed(ButtonItemViewModel item) => Save();

    // ── Presets ───────────────────────────────────────────────────────────────

    private void RebuildPresetTabs()
    {
        Presets.Clear();
        foreach (var p in _config.Config.Presets)
            Presets.Add(new PresetTabViewModel(p.Id, p.Name, tab => EditingPresetId = tab.Id));
        HasMultiplePresets = Presets.Count > 1;
        UpdateTabFlags();
    }

    private void UpdateTabFlags()
    {
        foreach (var tab in Presets)
        {
            tab.IsEditing = tab.Id == EditingPresetId;
            tab.IsLive = tab.Id == LivePresetId;
        }
    }

    partial void OnEditingPresetIdChanged(string value)
    {
        IsRenaming = false;
        UpdateTabFlags();
        foreach (var item in Items) item.Reload();
    }

    partial void OnLivePresetIdChanged(string value)
    {
        UpdateTabFlags();
        RefreshLights();
    }

    /// <summary>Called (on the UI thread) when the runtime's live preset changed.</summary>
    public void OnRuntimePresetChanged(string presetId) => LivePresetId = presetId;

    /// <summary>Adds a preset that starts as a copy of the one being edited, and edits it.</summary>
    [RelayCommand]
    private void AddPreset()
    {
        var presets = _config.Config.Presets;
        var id = NewPresetId();
        var name = $"Preset {presets.Count + 1}";
        presets.Add(new PresetConfig { Id = id, Name = name });
        foreach (var button in Buttons)
            if (button.Actions.TryGetValue(EditingPresetId, out var source) && source is not null)
                button.Actions[id] = CloneJson(source, ConfigJsonContext.Default.ButtonBindingConfig);
        RebuildPresetTabs();
        RefreshPresetPickers();
        EditingPresetId = id;
        RenameText = name;
        IsRenaming = true;
        Save();
    }

    private string NewPresetId()
    {
        var existing = new HashSet<string>(_config.Config.Presets.Select(p => p.Id));
        for (int n = 2; ; n++)
            if (!existing.Contains($"preset-{n}")) return $"preset-{n}";
    }

    [RelayCommand]
    private void StartRename()
    {
        RenameText = EditingName;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CommitRename()
    {
        var name = RenameText.Trim();
        var preset = _config.Config.Presets.Find(p => p.Id == EditingPresetId);
        IsRenaming = false;
        if (preset is null || name.Length == 0 || name == preset.Name) return;
        preset.Name = name;
        var tab = Presets.FirstOrDefault(t => t.Id == preset.Id);
        if (tab is not null) tab.Name = name;
        OnPropertyChanged(nameof(EditingName));
        OnPropertyChanged(nameof(LiveName));
        OnPropertyChanged(nameof(BannerText));
        OnPropertyChanged(nameof(MakeLiveText));
        RefreshPresetPickers();
        Save();
    }

    [RelayCommand]
    private void DeletePreset()
    {
        var presets = _config.Config.Presets;
        if (presets.Count < 2) return;
        var index = presets.FindIndex(p => p.Id == EditingPresetId);
        if (index < 0) return;
        var preset = presets[index];
        var removedActions = new Dictionary<ButtonConfig, ButtonBindingConfig>();
        foreach (var button in Buttons)
            if (button.Actions.Remove(preset.Id, out var binding) && binding is not null)
                removedActions[button] = binding;
        presets.RemoveAt(index);
        var wasLive = LivePresetId == preset.Id;

        RebuildPresetTabs();
        RefreshPresetPickers();
        EditingPresetId = presets[0].Id;
        // The runtime already falls back to the first preset once the live one is gone (and so
        // raises no change); read it back rather than wait for an event.
        if (wasLive) _presets.SetActivePreset(presets[0].Id);
        LivePresetId = _presets.ActivePresetId;
        Save();

        _toast.Show($"Preset “{preset.Name}” deleted", () =>
        {
            presets.Insert(Math.Min(index, presets.Count), preset);
            foreach (var (button, binding) in removedActions)
                button.Actions[preset.Id] = binding;
            RebuildPresetTabs();
            RefreshPresetPickers();
            EditingPresetId = preset.Id;
            if (wasLive) _presets.SetActivePreset(preset.Id);
            LivePresetId = _presets.ActivePresetId;
            Save();
        });
    }

    /// <summary>Makes the edited preset the one the controller uses.</summary>
    [RelayCommand]
    private void MakeLive() => _presets.SetActivePreset(EditingPresetId);

    private void RefreshPresetPickers()
    {
        foreach (var item in Items)
        {
            foreach (var slot in item.Slots) slot.RefreshPresets();
            item.Refresh();
        }
    }

    // ── Add / reset / remove ──────────────────────────────────────────────────

    [RelayCommand]
    private void Add()
    {
        var cfg = new ButtonConfig();
        Buttons.Add(cfg);
        var item = new ButtonItemViewModel(cfg, Items.Count, _devices, this) { IsExpanded = true };
        Items.Add(item);
        Renumber();
        RefreshController();
        Save();
    }

    [RelayCommand]
    private void AddMissing()
    {
        var target = _config.Config.Controller?.Buttons.Count ?? 0;
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

    public void Reset(ButtonItemViewModel item)
    {
        var before = CloneJson(item.Config, ConfigJsonContext.Default.ButtonConfig);
        item.Config.Actions.Clear();
        item.Config.LongPressMs = new ButtonConfig().LongPressMs;
        item.Config.DoublePressWindowMs = new ButtonConfig().DoublePressWindowMs;
        item.IsAdvancedOpen = false;
        item.Reload();
        Save();
        _toast.Show($"{item.Title} reset", () =>
        {
            item.Config.Actions = before.Actions;
            item.Config.LongPressMs = before.LongPressMs;
            item.Config.DoublePressWindowMs = before.DoublePressWindowMs;
            item.Reload();
            Save();
        });
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
        _toast.Show($"{item.Title} removed", () =>
        {
            Buttons.Add(cfg);
            Items.Add(item);
            Renumber();
            RefreshController();
            Save();
        });
    }

    // ── Reordering ────────────────────────────────────────────────────────────

    public int Count => Items.Count;

    public void MoveUp(ButtonItemViewModel item)
    {
        var i = Items.IndexOf(item);
        if (i > 0) Move(i, i - 1);
    }

    public void MoveDown(ButtonItemViewModel item)
    {
        var i = Items.IndexOf(item);
        if (i >= 0 && i < Items.Count - 1) Move(i, i + 1);
    }

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

    public void PreviewMove(int from, int to)
    {
        for (int i = 0; i < Items.Count; i++)
            Items[i].Index = SlidersViewModel.PreviewIndex(i, from, to);
    }

    public void CancelPreview() => Renumber();

    private void Renumber()
    {
        for (int i = 0; i < Items.Count; i++)
        {
            Items[i].Index = i;
            Items[i].IsLast = i == Items.Count - 1;
        }
    }

    // ── Live updates (UI thread) ──────────────────────────────────────────────

    /// <summary>Flashes the card of the button at hardware input <paramref name="index"/>.</summary>
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

    /// <summary>The connection type changed: the "Runs only over USB" notes follow.</summary>
    public void RefreshTransport()
    {
        foreach (var item in Items) item.RefreshTransport();
    }

    public void RefreshDevices(IReadOnlyList<AudioDevice> devices)
    {
        _devices = devices;
        foreach (var item in Items) item.RefreshDevices(devices);
        RefreshLights();
    }

    private static T CloneJson<T>(T value, JsonTypeInfo<T> type)
        => JsonSerializer.Deserialize(JsonSerializer.Serialize(value, type), type)!;
}
