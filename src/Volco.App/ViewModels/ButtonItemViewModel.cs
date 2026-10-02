using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;

namespace Volco.App.ViewModels;

/// <summary>
/// One button card. Its position (= <see cref="Index"/>) is the hardware input number. Timing is
/// shared by every preset; the three action rows show the preset being edited, and the light
/// preview shows what the controller shows now, for the live preset.
/// </summary>
public sealed partial class ButtonItemViewModel : ObservableObject
{
    private readonly IButtonCardOwner _owner;
    private bool _loading;

    internal ButtonConfig Config { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private int _index;

    public string Title => $"Button {Index}";

    [ObservableProperty]
    private bool _isLast;

    public ActionSlotViewModel Press { get; }
    public ActionSlotViewModel DoublePress { get; }
    public ActionSlotViewModel Hold { get; }

    // ── Timing (shared by every preset) ───────────────────────────────────────

    [ObservableProperty]
    private int _longPressMs;

    [ObservableProperty]
    private int _doublePressWindowMs;

    // ── Derived display ───────────────────────────────────────────────────────

    [ObservableProperty]
    private string _summary = "";

    /// <summary>No action in any preset: the card is drawn as "Not set up".</summary>
    [ObservableProperty]
    private bool _isNotSetUp;

    [ObservableProperty]
    private string _lightDescription = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnOffLight), nameof(IsPairLight), nameof(IsPresetLight), nameof(HasNoLight))]
    private LightKind _lightKind;

    public bool IsOnOffLight => LightKind == LightKind.OnOff;
    public bool IsPairLight => LightKind == LightKind.Pair;
    public bool IsPresetLight => LightKind == LightKind.Preset;
    public bool HasNoLight => LightKind == LightKind.None;

    /// <summary>The value the controller is sent for this button (spec §3.6).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LedOn), nameof(LedA), nameof(LedB))]
    private byte _lightValue;

    public bool LedOn => LightValue != 0;
    public bool LedA => LightValue == 1;
    public bool LedB => LightValue == 2;

    /// <summary>One dot per preset for preset actions; the live one is lit.</summary>
    [ObservableProperty]
    private IReadOnlyList<bool> _presetDots = Array.Empty<bool>();

    // ── View state ────────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isAdvancedOpen;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void MoveUp() => _owner.MoveUp(this);

    [RelayCommand]
    private void MoveDown() => _owner.MoveDown(this);

    [RelayCommand]
    private void Reset() => _owner.Reset(this);

    [RelayCommand]
    private void RemoveLast() => _owner.RemoveLast();

    internal ButtonItemViewModel(ButtonConfig cfg, int index, IReadOnlyList<AudioDevice> devices, IButtonCardOwner owner)
    {
        Config = cfg;
        _owner = owner;
        _index = index;
        Press = new ActionSlotViewModel("Press", devices, owner.PresetChoices, owner.Execute, OnActionChanged);
        DoublePress = new ActionSlotViewModel("Double press", devices, owner.PresetChoices, owner.Execute, OnActionChanged);
        Hold = new ActionSlotViewModel("Hold", devices, owner.PresetChoices, owner.Execute, OnActionChanged);
        Reload();
    }

    internal IEnumerable<ActionSlotViewModel> Slots
    {
        get { yield return Press; yield return DoublePress; yield return Hold; }
    }

    /// <summary>Re-reads timing and the edited preset's actions (after a preset switch, Reset or Undo).</summary>
    internal void Reload()
    {
        _loading = true;
        try
        {
            LongPressMs = Config.LongPressMs;
            DoublePressWindowMs = Config.DoublePressWindowMs;
        }
        finally { _loading = false; }

        var binding = Config.GetOrAddBinding(_owner.EditingPresetId);
        Press.Bind(binding.SinglePress);
        DoublePress.Bind(binding.DoublePress);
        Hold.Bind(binding.LongPress);
        Refresh();
    }

    /// <summary>Recomputes the summary and the light preview. Cheap; safe to call often.</summary>
    internal void Refresh()
    {
        var edited = Config.BindingFor(_owner.EditingPresetId);
        IsNotSetUp = Config.Actions.Values.All(b => b is null || b.IsEmpty);
        var summary = ActionCatalog.Summary(edited, _owner.PresetName);
        Summary = summary.Length > 0 ? summary : IsNotSetUp ? "Not set up" : "Nothing in this preset";
        LightDescription = ActionCatalog.LightDescription(edited);

        // The light preview is what the controller shows now, so it follows the live preset.
        var live = Config.BindingFor(_owner.LivePresetId);
        var source = ActionCatalog.LightSource(live);
        LightKind = source is { } s ? ActionCatalog.LightKindOf(s.Action.Action) : LightKind.None;
        LightValue = ControllerDisplay.LedValueFor(live, _owner.AppConfig, _owner.LivePresetId, _owner.Audio);
        PresetDots = LightKind == LightKind.Preset
            ? _owner.AppConfig.Presets.Select((p, i) => LightValue == i + 1).ToList()
            : Array.Empty<bool>();
    }

    private void OnActionChanged()
    {
        Refresh();
        _owner.Changed(this);
    }

    partial void OnLongPressMsChanged(int value)
    {
        if (_loading) return;
        Config.LongPressMs = value;
        _owner.Changed(this);
    }

    partial void OnDoublePressWindowMsChanged(int value)
    {
        if (_loading) return;
        Config.DoublePressWindowMs = value;
        _owner.Changed(this);
    }

    public void Pulse()
    {
        IsActive = false;
        IsActive = true;
    }

    internal void RefreshDevices(IReadOnlyList<AudioDevice> devices)
    {
        foreach (var slot in Slots) slot.RefreshDevices(devices);
    }
}

/// <summary>What a button card asks of the Buttons page that owns it.</summary>
internal interface IButtonCardOwner
{
    string EditingPresetId { get; }
    string LivePresetId { get; }
    AppConfig AppConfig { get; }
    IAudioEngine Audio { get; }
    IReadOnlyList<PresetChoice> PresetChoices();
    string? PresetName(string? id);
    void Execute(ButtonActionConfig action);
    void Changed(ButtonItemViewModel item);
    void MoveUp(ButtonItemViewModel item);
    void MoveDown(ButtonItemViewModel item);
    void Reset(ButtonItemViewModel item);
    void RemoveLast();
}
