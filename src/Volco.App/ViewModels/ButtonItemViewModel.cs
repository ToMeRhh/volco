using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.App.ViewModels.Hardware;
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Input;
using Volco.Core.Input.Actions;
using Volco.Core.Runtime;

namespace Volco.App.ViewModels;

/// <summary>One action of a button tile's "other actions" list: its gesture, label and icon
/// (<see cref="IconKey"/> is the action's <see cref="ButtonActionType"/> name).</summary>
public sealed record GestureLine(PressType Gesture, string Label, string IconKey);

/// <summary>
/// One button of the Controls page. Its position (= <see cref="Index"/>) is the hardware input
/// number. Timing is shared by every mode; the action rows and the light and screen previews show
/// the mode being edited (when it is live, exactly what the controller shows).
/// </summary>
public sealed partial class ButtonItemViewModel : ObservableObject
{
    private readonly IButtonCardOwner _owner;
    private bool _loading;

    internal ButtonConfig Config { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading))]
    private int _index;

    // Only what depends on the input number changes here.
    partial void OnIndexChanged(int value)
    {
        Indicator = _owner.IndicatorFor(value);
        UpdateLightDescription(Config.BindingFor(_owner.EditingModeId));
    }

    private void UpdateLightDescription(ButtonBindingConfig edited)
        => LightDescription = IsScreen
            ? ActionCatalog.ScreenDescription(edited)
            : ActionCatalog.LightDescription(edited, IsLed, _owner.ModeName, _owner.DeviceName);

    /// <summary>The button's name everywhere it is shown (the inspector, toasts), numbered from 1.</summary>
    public string Heading => $"Button {Index + 1}";

    /// <summary>The tile's captions on Hardware; null until the Hardware page fills them.</summary>
    [ObservableProperty]
    private TileWiring? _wiring;

    /// <summary>The Controls page's selected tile.</summary>
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isLast;

    public ActionSlotViewModel Press { get; }
    public ActionSlotViewModel DoublePress { get; }
    public ActionSlotViewModel Hold { get; }

    // ── Timing (shared by every mode) ───────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimingSummary))]
    private int _longPressMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimingSummary))]
    private int _doublePressWindowMs;

    /// <summary>The inspector's Timing summary.</summary>
    public string TimingSummary => $"{LongPressMs} ms · {DoublePressWindowMs} ms";

    // ── Derived display ───────────────────────────────────────────────────────

    /// <summary>The edited mode's main action (the one the light follows), or "Not set".</summary>
    [ObservableProperty]
    private string _mainLabel = "Not set";

    /// <summary>Which gesture the main action is on; null when the edited mode has none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LightRowText))]
    private PressType? _mainGesture;

    /// <summary>The edited mode's other actions, in Press, Double press, Hold order.</summary>
    [ObservableProperty]
    private IReadOnlyList<GestureLine> _secondary = Array.Empty<GestureLine>();

    /// <summary>The edited mode has an action, so Clear is offered.</summary>
    [ObservableProperty]
    private bool _hasEditedActions;

    /// <summary>The config has this button but the controller's last Hello doesn't (spec §4.1).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSwap))]
    private bool _isMissing;

    /// <summary>A control only the controller draft has (added with + Add, made by
    /// <c>CreateDetached</c>): its tile says to save first (controller-page spec §4.1).</summary>
    public bool IsNew { get; init; }

    /// <summary>The tile's grip is there: the button is in Volco's settings and on the controller.</summary>
    public bool CanSwap => !IsMissing && !IsNew;

    [ObservableProperty]
    private string _lightDescription = "";

    /// <summary>What the controller reported for this input (spec §6.3); null before the first Hello.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLed), nameof(IsLedPair), nameof(IsScreen), nameof(KindText), nameof(HasLightRow), nameof(LightRowLabel))]
    private IndicatorKind? _indicator;

    public bool IsLed => Indicator == IndicatorKind.Led;
    public bool IsLedPair => Indicator == IndicatorKind.LedPair;
    public bool IsScreen => Indicator == IndicatorKind.Screen;

    /// <summary>The inspector header's kind; empty before the first Hello.</summary>
    public string KindText => Indicator switch
    {
        null => "",
        IndicatorKind.Led => "LED",
        IndicatorKind.LedPair => "LED pair",
        IndicatorKind.Screen => "Screen",
        _ => "No light",
    };

    /// <summary>The inspector's Light or Screen row; a button without a light has none, unless the
    /// controller draft gives it one.</summary>
    public bool HasLightRow => Indicator is IndicatorKind.Led or IndicatorKind.LedPair or IndicatorKind.Screen || IsLightChanged;

    public string LightRowLabel => IsScreen ? "Screen" : "Light";

    /// <summary>"Follows Press" (the main action's gesture), or "Off" with no action in the edited
    /// mode; "Follows the new light after saving" while the draft changes its light.</summary>
    public string LightRowText => IsLightChanged ? "Follows the new light after saving"
        : MainGesture is { } gesture ? $"Follows {GestureName(gesture)}" : "Off";

    /// <summary>The controller draft gives this button another kind of light than the controller
    /// has (its Hello): until it is saved, the light row says so (controller-page spec §4.3). Set by
    /// the Hardware view model.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLightRow), nameof(LightRowText))]
    private bool _isLightChanged;

    public static string GestureName(PressType gesture) => gesture switch
    {
        PressType.Double => "Double press",
        PressType.Long => "Hold",
        _ => "Press",
    };

    /// <summary>What this button's screen shows in the edited mode.</summary>
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
    private bool _isAdvancedOpen;

    [RelayCommand]
    private void RemoveLast() => _owner.RemoveLast();

    internal ButtonItemViewModel(ButtonConfig cfg, int index, IReadOnlyList<AudioDevice> devices, IButtonCardOwner owner)
    {
        Config = cfg;
        _owner = owner;
        _index = index;
        Press = new ActionSlotViewModel("Press", devices, owner.ModeChoices, a => owner.Execute(a, Index, PressType.Single),
                                        OnActionChanged, name => owner.Describe(name), owner.SlotHost);
        DoublePress = new ActionSlotViewModel("Double press", devices, owner.ModeChoices, a => owner.Execute(a, Index, PressType.Double),
                                              OnActionChanged, name => owner.Describe(name), owner.SlotHost);
        Hold = new ActionSlotViewModel("Hold", devices, owner.ModeChoices, a => owner.Execute(a, Index, PressType.Long),
                                       OnActionChanged, name => owner.Describe(name), owner.SlotHost);
        Reload();
    }

    internal IEnumerable<ActionSlotViewModel> Slots
    {
        get { yield return Press; yield return DoublePress; yield return Hold; }
    }

    /// <summary>Re-reads timing and the edited mode's actions (after a mode switch).</summary>
    internal void Reload()
    {
        _loading = true;
        try
        {
            LongPressMs = Config.LongPressMs;
            DoublePressWindowMs = Config.DoublePressWindowMs;
        }
        finally { _loading = false; }

        var binding = Config.GetOrAddBinding(_owner.EditingModeId);
        Press.Bind(binding.SinglePress);
        DoublePress.Bind(binding.DoublePress);
        Hold.Bind(binding.LongPress);
        Refresh();
        RefreshProblems();
    }

    /// <summary>Recomputes the action lines, the rows' labels and the light/screen preview. Cheap; safe
    /// to call often (no file or device lookups).</summary>
    internal void Refresh()
    {
        var edited = Config.BindingFor(_owner.EditingModeId);
        RefreshActionLines(edited);

        Indicator = _owner.IndicatorFor(Index);
        UpdateLightDescription(edited);

        // The previews show what the controller would show in the edited mode, through the same
        // function, with a press's feedback (an error flash, the pressed action) and, in the live
        // mode, a mode change's announce.
        var feedback = _owner.Feedback?.FeedbackFor(Index);
        var announce = _owner.EditingModeId == _owner.LiveModeId ? _owner.Feedback?.AnnounceStep : null;
        var ledKind = Indicator == IndicatorKind.Led ? IndicatorKind.Led : IndicatorKind.LedPair;
        LightValue = (ControllerDisplay.For(edited, ledKind, _owner.AppConfig, _owner.EditingModeId, _owner.Audio,
                                            announce, _owner.ActionStatus, feedback) as LedDisplay)?.Value ?? 0;
        Screen = (ControllerDisplay.For(edited, IndicatorKind.Screen, _owner.AppConfig, _owner.EditingModeId, _owner.Audio,
                                        announce, _owner.ActionStatus, feedback) as ScreenDisplay)?.Content ?? ScreenContent.Blank;
    }

    /// <summary>The main action (the first set one, Press → Double press → Hold, as the light
    /// follows it) and the others.</summary>
    private void RefreshActionLines(ButtonBindingConfig edited)
    {
        var others = new List<GestureLine>();
        PressType? main = null;
        var mainLabel = "Not set";
        foreach (var (gesture, action) in new[] { (PressType.Single, edited.SinglePress), (PressType.Double, edited.DoublePress), (PressType.Long, edited.LongPress) })
        {
            if (action.Action == ButtonActionType.None) continue;
            var label = ActionCatalog.ShortLabel(action, _owner.ModeName, _owner.DeviceName);
            if (main is null) { main = gesture; mainLabel = label; }
            else others.Add(new GestureLine(gesture, label, action.Action.ToString()));
        }
        MainGesture = main;
        MainLabel = mainLabel;
        HasEditedActions = main is not null;
        if (!others.SequenceEqual(Secondary)) Secondary = others;

        // The inspector's rows: label and main. Problems are checked in RefreshProblems.
        foreach (var (slot, gesture) in new[] { (Press, PressType.Single), (DoublePress, PressType.Double), (Hold, PressType.Long) })
        {
            var action = slot.Config;
            var set = action.Action != ButtonActionType.None;
            slot.SetRow(set ? ActionCatalog.ShortLabel(action, _owner.ModeName, _owner.DeviceName) : "—", gesture == main);
        }
    }

    /// <summary>Checks each saved action (spec §6.4, on load) for the inspector's warning. It may
    /// probe files, so it runs only when an action, the modes or the devices change, never from
    /// <see cref="Refresh"/>, which every volume change calls.</summary>
    internal void RefreshProblems()
    {
        var context = _owner.ValidationContext();
        foreach (var slot in Slots)
            slot.Problem = slot.Config.Action == ButtonActionType.None
                ? null
                : ActionValidator.Validate(slot.Config, context).Values.FirstOrDefault();
    }

    /// <summary>The row of one gesture.</summary>
    internal ActionSlotViewModel SlotFor(PressType gesture) => gesture switch
    {
        PressType.Double => DoublePress,
        PressType.Long => Hold,
        _ => Press,
    };

    /// <summary>A row for the action dialog's settings pane, over <paramref name="draft"/> (a copy;
    /// nothing is saved): this button's devices, modes and services, and the real validation.</summary>
    internal ActionSlotViewModel CreateDraft(PressType gesture, ButtonActionConfig draft)
    {
        var slot = new ActionSlotViewModel(GestureName(gesture), SlotFor(gesture).Devices, _owner.ModeChoices,
                                           _ => { }, () => { }, name => _owner.Describe(name), _owner.SlotHost)
        {
            Validation = _owner.ValidationContext,
        };
        slot.Bind(draft);
        return slot;
    }

    /// <summary>The action dialog's Done or Remove: <paramref name="action"/> becomes
    /// <paramref name="gesture"/>'s action in <paramref name="modeId"/>, then the row, the
    /// previews and the problems follow and the config is saved, as after an edit on the row.</summary>
    internal void SetAction(string modeId, PressType gesture, ButtonActionConfig action)
    {
        var binding = Config.GetOrAddBinding(modeId);
        switch (gesture)
        {
            case PressType.Double: binding.DoublePress = action; break;
            case PressType.Long: binding.LongPress = action; break;
            default: binding.SinglePress = action; break;
        }
        if (modeId == _owner.EditingModeId) SlotFor(gesture).Bind(action);
        OnActionChanged();
    }

    private void OnActionChanged()
    {
        Refresh();
        RefreshProblems();
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

    /// <summary>A media session, program or script changed: the media pickers and the preview follow.</summary>
    internal void RefreshActionState()
    {
        foreach (var slot in Slots) slot.RefreshMediaApps();
        Refresh();
    }
}

/// <summary>What a button asks of the list that owns it.</summary>
internal interface IButtonCardOwner
{
    string EditingModeId { get; }
    string LiveModeId { get; }
    AppConfig AppConfig { get; }
    IAudioEngine Audio { get; }
    IReadOnlyList<ModeChoice> ModeChoices();
    string? ModeName(string? id);
    /// <summary>Test: runs <paramref name="action"/> once, as button <paramref name="button"/>'s
    /// <paramref name="gesture"/> would.</summary>
    void Execute(ButtonActionConfig action, int button, PressType gesture);
    ActionSlotHost SlotHost { get; }
    IActionStatus ActionStatus { get; }
    /// <summary>A press's feedback and a mode change's announce; null: none.</summary>
    IButtonFeedback? Feedback { get; }
    void Changed(ButtonItemViewModel item);
    void RemoveLast();
    IndicatorKind? IndicatorFor(int index);
    string DeviceName(DeviceRefConfig? device);
    /// <summary>The validation context with the real system: plugged-in devices and files.</summary>
    ActionValidationContext ValidationContext();
    DeviceChoice Describe(string friendlyName);
}
