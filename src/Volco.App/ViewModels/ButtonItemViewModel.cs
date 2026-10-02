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

    // A drag preview renumbers cards without moving their config: only what depends on the
    // input number changes here, so each step stays cheap.
    partial void OnIndexChanged(int value)
    {
        Indicator = _owner.IndicatorFor(value);
        UpdateLightDescription(Config.BindingFor(_owner.EditingPresetId));
    }

    private void UpdateLightDescription(ButtonBindingConfig edited)
        => LightDescription = IsScreen ? ActionCatalog.ScreenDescription(edited) : ActionCatalog.LightDescription(edited);

    public bool ShowLightDescription => Indicator is not null && Indicator != IndicatorKind.None;

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

    /// <summary>What the controller reported for this input (spec §6.3); null before the first Hello.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLed), nameof(IsLedPair), nameof(IsScreen), nameof(IsIndicatorUnknown), nameof(IndicatorText), nameof(ShowLightDescription))]
    private IndicatorKind? _indicator;

    public bool IsLed => Indicator == IndicatorKind.Led;
    public bool IsLedPair => Indicator == IndicatorKind.LedPair;
    public bool IsScreen => Indicator == IndicatorKind.Screen;
    public bool IsIndicatorUnknown => Indicator is null;

    public string IndicatorText => Indicator switch
    {
        null => "Connect your controller to see its lights and screens.",
        IndicatorKind.Led => "Light: LED",
        IndicatorKind.LedPair => "Light: LED pair",
        IndicatorKind.Screen => "Light: Screen",
        _ => "Light: None",
    };

    /// <summary>What this button's screen shows in the edited preset.</summary>
    [ObservableProperty]
    private ScreenContent _screen = ScreenContent.Blank;

    /// <summary>The value the controller is sent for this button (spec §3.6).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LedOn), nameof(LedA), nameof(LedB))]
    private byte _lightValue;

    public bool LedOn => LightValue != 0;
    public bool LedA => LightValue == 1;
    public bool LedB => LightValue == 2;

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
        Press = new ActionSlotViewModel("Press", devices, owner.PresetChoices, owner.Execute, OnActionChanged, name => owner.Describe(name));
        DoublePress = new ActionSlotViewModel("Double press", devices, owner.PresetChoices, owner.Execute, OnActionChanged, name => owner.Describe(name));
        Hold = new ActionSlotViewModel("Hold", devices, owner.PresetChoices, owner.Execute, OnActionChanged, name => owner.Describe(name));
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

    /// <summary>Recomputes the summary and the light/screen preview. Cheap; safe to call often.</summary>
    internal void Refresh()
    {
        var edited = Config.BindingFor(_owner.EditingPresetId);
        IsNotSetUp = Config.Actions.Values.All(b => b is null || b.IsEmpty);
        var summary = ActionCatalog.Summary(edited, _owner.PresetName, _owner.DeviceName);
        Summary = summary.Length > 0 ? summary : IsNotSetUp ? "Not set up" : "Nothing in this preset";

        Indicator = _owner.IndicatorFor(Index);
        UpdateLightDescription(edited);

        // LEDs show what the controller shows now (the live preset); a screen preview shows the
        // preset being edited, so editing it is visible.
        var live = Config.BindingFor(_owner.LivePresetId);
        LightValue = ControllerDisplay.LedValueFor(live, _owner.AppConfig, _owner.LivePresetId, _owner.Audio);
        // From this card's own config, not AppConfig.Buttons[Index]: during a drag preview the
        // card's Index no longer matches its position in the list.
        Screen = ControllerDisplay.Screen(edited.All().FirstOrDefault(a => a.Action != ButtonActionType.None),
                                          _owner.AppConfig, _owner.EditingPresetId, _owner.Audio);
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
    IndicatorKind? IndicatorFor(int index);
    string DeviceName(DeviceRefConfig? device);
    DeviceChoice Describe(string friendlyName);
}
