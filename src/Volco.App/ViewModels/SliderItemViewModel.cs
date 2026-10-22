using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.App.ViewModels.Hardware;
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Programs;

namespace Volco.App.ViewModels;

/// <summary>
/// One slider of the Controller page. Its position in <see cref="SlidersViewModel.Items"/>
/// (= <see cref="Index"/>) is the hardware input number. Edits write straight through to the
/// wrapped <see cref="SliderConfig"/> and request a save.
/// </summary>
public partial class SliderItemViewModel : ObservableObject, IDeviceLabeler
{
    /// <summary>The device picker's first entry: a null <see cref="SliderConfig.Device"/>.</summary>
    public const string CurrentDefaultOutput = "Main output";

    private readonly Action _save;
    private readonly ISliderCardOwner _owner;
    private bool _loading;

    internal SliderConfig Config { get; }

    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>The hardware input number (= list position). Relabelled live while dragging.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading), nameof(DisplayHeading))]
    private int _index;

    /// <summary>The slider's name everywhere it is shown (the inspector, toasts), numbered from 1.</summary>
    public string Heading => $"Slider {Index + 1}";

    /// <summary>"Slider N" at its place in the controller draft while the draft draws it (set by
    /// the Hardware view model; an unsaved removal moves the later ones up), else null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayHeading))]
    private string? _draftHeading;

    /// <summary>The name the Controller page shows (the inspector header, the tile's tooltip):
    /// the draft's number while the draft draws it (hardware-page spec §6.5), else <see cref="Heading"/>.</summary>
    public string DisplayHeading => DraftHeading ?? Heading;

    /// <summary>The Controller page's selected tile.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>The user's name for the slider; "" means unnamed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label), nameof(IsUnset))]
    private string _name = "";

    /// <summary>The tile's label: the name, else what it controls, else "Not set".</summary>
    public string Label => Name?.Trim() is { Length: > 0 } name ? name : TargetLabel;

    /// <summary>What it controls, or "Not set": the Controls row, and the name box's placeholder.
    /// A device reads as its display name (its nickname, else the short automatic name).</summary>
    public string TargetLabel => IsNotSetUp ? "Not set" : IsDevice ? Describe(SelectedDevice).Label : Summary;

    /// <summary>No name and nothing to control: the label "Not set" is drawn quieter.</summary>
    public bool IsUnset => string.IsNullOrWhiteSpace(Name) && IsNotSetUp;

    /// <summary>The Controls row's icon (a resource key).</summary>
    public string TargetIconKey => MappingType switch
    {
        SliderMappingType.Device => "Icon.Speaker",
        SliderMappingType.Processes => "Icon.Device.App",
        SliderMappingType.Unmapped => "Icon.Volume",
        _ => "Icon.Plus",
    };

    /// <summary>An apps slider's icon: the first of its apps whose icon Volco has seen this run
    /// (<see cref="Platform.AppIconLookup"/>); null otherwise, and the tile shows none and the
    /// inspector <see cref="TargetIconKey"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAppIcon))]
    private Bitmap? _appIcon;

    public bool HasAppIcon => AppIcon is not null;

    /// <summary>True for the last slider: only it can be removed.</summary>
    [ObservableProperty]
    private bool _isLast;

    /// <summary>The config has this slider but the controller's last Hello doesn't (spec §4.1).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSwap))]
    private bool _isMissing;

    /// <summary>A control only the controller draft has (added with + Add, made by
    /// <c>CreateDetached</c>): its tile says to save first (controller-page spec §4.1).</summary>
    public bool IsNew { get; init; }

    /// <summary>The tile's grip is there: the slider is in Volco's settings and on the controller.</summary>
    public bool CanSwap => !IsMissing && !IsNew;

    // ── What it controls ──────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApps), nameof(IsOtherApps), nameof(IsDevice), nameof(IsNotSetUp), nameof(Summary),
                              nameof(IsUnset), nameof(TargetIconKey))]
    private SliderMappingType _mappingType;

    public bool IsApps
    {
        get => MappingType == SliderMappingType.Processes;
        set { if (value) MappingType = SliderMappingType.Processes; }
    }

    public bool IsOtherApps
    {
        get => MappingType == SliderMappingType.Unmapped;
        set { if (value) MappingType = SliderMappingType.Unmapped; }
    }

    public bool IsDevice
    {
        get => MappingType == SliderMappingType.Device;
        set { if (value) MappingType = SliderMappingType.Device; }
    }

    /// <summary>"Not set up": a new slider, until the target dialog sets it.</summary>
    public bool IsNotSetUp => MappingType == SliderMappingType.None;

    /// <summary>The apps (image names) an Apps slider controls.</summary>
    public IReadOnlyList<string> AppNames => Config.Processes;

    /// <summary>"Main output" followed by every render device.</summary>
    [ObservableProperty]
    private List<string> _deviceChoices = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _selectedDevice = CurrentDefaultOutput;

    /// <summary>For "All other apps": which apps it leaves to the other sliders.</summary>
    [ObservableProperty]
    private string _otherAppsHint = "";

    public string Summary => MappingType switch
    {
        // The apps as the target dialog names them: "spotify.exe" reads "spotify".
        SliderMappingType.Processes => AppNames.Count > 0 ? string.Join(", ", AppNames.Select(CommandPath.FileNameWithoutExtension)) : "No apps yet",
        SliderMappingType.Unmapped => "All other apps",
        SliderMappingType.Device => SelectedDevice,
        _ => "Not set up",
    };

    // ── Advanced ──────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResponseSummary))]
    private bool _invert;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurveIndex), nameof(ResponseSummary))]
    private ResponseCurve _curve;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResponseSummary))]
    private double _deadZonePercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResponseSummary))]
    private double _smoothingFactor;

    /// <summary>The inspector's Response summary: "Linear" with every setting at its default.</summary>
    public string ResponseSummary
        => Curve == ResponseCurve.Linear && DeadZonePercent == 0 && SmoothingFactor == 0 && !Invert ? "Linear" : "Custom";

    /// <summary>Order matches the curve picker: Linear, Logarithmic, Exponential, S-Curve.</summary>
    public int CurveIndex
    {
        get => Curve switch
        {
            ResponseCurve.Logarithmic => 1,
            ResponseCurve.Exponential => 2,
            ResponseCurve.SCurve => 3,
            _ => 0,
        };
        set
        {
            if (value < 0) return;
            Curve = value switch
            {
                1 => ResponseCurve.Logarithmic,
                2 => ResponseCurve.Exponential,
                3 => ResponseCurve.SCurve,
                _ => ResponseCurve.Linear,
            };
        }
    }

    // ── View state ────────────────────────────────────────────────────────────

    /// <summary>The tile's captions on Hardware; null until the Controller page fills them.</summary>
    [ObservableProperty]
    private TileWiring? _wiring;

    /// <summary>Live volume 0..1 from the controller; display only, never saved.</summary>
    [ObservableProperty]
    private double _liveLevel;

    /// <summary>Set (false → true) whenever the controller reports this slider, so the tile
    /// flashes; the view's style animation does the fading.</summary>
    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isAdvancedOpen;

    [RelayCommand]
    private void RemoveLast() => _owner.RemoveLast();

    public DeviceChoice Describe(string friendlyName)
        => friendlyName == CurrentDefaultOutput ? new DeviceChoice(CurrentDefaultOutput, "") : _owner.Describe(friendlyName);

    // ── Construction ──────────────────────────────────────────────────────────

    internal SliderItemViewModel(SliderConfig cfg, int index, IReadOnlyList<AudioDevice> devices, ISliderCardOwner owner, Action save)
    {
        Config = cfg;
        _owner = owner;
        _save = save;
        _index = index;
        SetDevices(devices);
        Reload();
    }

    /// <summary>Re-reads every field from <see cref="Config"/> without saving (after the target dialog changed it).</summary>
    internal void Reload()
    {
        _loading = true;
        try
        {
            Name = Config.Name ?? "";
            MappingType = Config.Type;
            SelectedDevice = Config.Device is { FriendlyName.Length: > 0 } configured
                ? configured.FriendlyName
                : CurrentDefaultOutput;
            Invert = Config.Invert;
            Curve = Config.Curve;
            DeadZonePercent = Config.DeadZonePercent;
            SmoothingFactor = Config.SmoothingFactor;
        }
        finally { _loading = false; }
        OnPropertyChanged(nameof(Summary));
    }

    // ── Write-through ─────────────────────────────────────────────────────────

    // Label and TargetLabel read Summary, which is raised from several places.
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != nameof(Summary)) return;
        OnPropertyChanged(nameof(TargetLabel));
        OnPropertyChanged(nameof(Label));
    }

    partial void OnNameChanged(string value)
    {
        if (_loading) return;
        Config.Name = value ?? "";
        Persist();
    }

    partial void OnMappingTypeChanged(SliderMappingType value)
    {
        if (_loading) return;
        Config.Type = value;
        _owner.AppsChanged();
        Persist();
    }

    partial void OnSelectedDeviceChanged(string value)
    {
        // Swapping the picker's ItemsSource (a device was plugged in or out) can drive its
        // two-way-bound SelectedItem to null for a moment. That is never the user's choice.
        if (_loading || string.IsNullOrEmpty(value)) return;
        Config.Device = value == CurrentDefaultOutput ? null : DeviceRefLookup.DeviceRefFromName(_devices, value);
        Persist();
    }

    partial void OnInvertChanged(bool value)
    {
        if (_loading) return;
        Config.Invert = value;
        Persist();
    }

    partial void OnCurveChanged(ResponseCurve value)
    {
        if (_loading) return;
        Config.Curve = value;
        Persist();
    }

    partial void OnDeadZonePercentChanged(double value)
    {
        // Two decimals: the slider control is continuous.
        var rounded = Quantise(value);
        if (rounded != value) { DeadZonePercent = rounded; return; }
        if (_loading) return;
        Config.DeadZonePercent = value;
        Persist();
    }

    partial void OnSmoothingFactorChanged(double value)
    {
        var rounded = Quantise(value);
        if (rounded != value) { SmoothingFactor = rounded; return; }
        if (_loading) return;
        Config.SmoothingFactor = value;
        Persist();
    }

    private static double Quantise(double v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    private void Persist() => _save();

    // ── Live updates (UI thread) ──────────────────────────────────────────────

    /// <summary>Marks the tile active again, restarting its flash.</summary>
    public void Pulse()
    {
        IsActive = false;
        IsActive = true;
    }

    internal void SetOtherAppsHint(IReadOnlyCollection<string> claimedByOthers)
        => OtherAppsHint = claimedByOthers.Count == 0
            ? "Controls every app that no other slider lists."
            : $"Controls every app that no other slider lists (everything except {string.Join(", ", claimedByOthers)}).";

    /// <summary>
    /// Re-seeds the device picker after a device was plugged in, removed or renamed. Re-reads the
    /// selection from the config (whose stored name the runtime keeps current) without saving; a
    /// device that is gone simply shows no selection.
    /// </summary>
    public void RefreshDevices(IReadOnlyList<AudioDevice> devices)
    {
        var keep = Config.Type == SliderMappingType.Device && Config.Device is { FriendlyName.Length: > 0 } configured
            ? configured.FriendlyName
            : SelectedDevice;
        _loading = true;
        try
        {
            SetDevices(devices);
            // Pass through no selection, so the selection box re-runs its label template even
            // when the selected name is unchanged (a nickname may have changed).
            SelectedDevice = "";
            SelectedDevice = keep;
        }
        finally { _loading = false; }
        OnPropertyChanged(nameof(SelectedDevice));
        OnPropertyChanged(nameof(Summary));   // a nickname may have changed: the labels follow
    }

    private IReadOnlyList<AudioDevice> _devices = Array.Empty<AudioDevice>();

    private void SetDevices(IReadOnlyList<AudioDevice> devices)
    {
        _devices = devices;
        var choices = new List<string> { CurrentDefaultOutput };
        choices.AddRange(devices.Select(device => device.FriendlyName));
        // A friendly-name-only device that is not plugged in still shows as selected.
        if (Config.Device is { FriendlyName.Length: > 0 } configured
            && !choices.Contains(configured.FriendlyName, StringComparer.OrdinalIgnoreCase))
            choices.Add(configured.FriendlyName);
        DeviceChoices = choices;
    }
}

/// <summary>What a slider asks of the list that owns it.</summary>
internal interface ISliderCardOwner
{
    void RemoveLast();
    void AppsChanged();
    DeviceChoice Describe(string friendlyName);
}
