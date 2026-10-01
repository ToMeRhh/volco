using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Audio;
using Volco.Core.Config;

namespace Volco.App.ViewModels;

/// <summary>
/// One slider card. Its position in <see cref="SlidersViewModel.Items"/> (= <see cref="Index"/>) is
/// the hardware input number; a slider has no name, only that number and what it controls. Edits
/// write straight through to the wrapped <see cref="SliderConfig"/> and request a save.
/// </summary>
public partial class SliderItemViewModel : ObservableObject
{
    /// <summary>The device picker's first entry: a null <see cref="SliderConfig.Device"/>.</summary>
    public const string CurrentDefaultOutput = "Current default output";

    private readonly IAudioEngine _audio;
    private readonly Action _save;
    private readonly ISliderCardOwner _owner;
    private bool _loading;

    internal SliderConfig Config { get; }

    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>The hardware input number (= list position). Relabelled live while dragging.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private int _index;

    public string Title => $"Slider {Index}";

    /// <summary>True for the last card: only it offers "Remove last slider".</summary>
    [ObservableProperty]
    private bool _isLast;

    // ── What it controls ──────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApps), nameof(IsOtherApps), nameof(IsDevice), nameof(IsNotSetUp), nameof(Summary))]
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

    /// <summary>"Not set up": only Reset (or a new card) gets here; the three choices leave it.</summary>
    public bool IsNotSetUp => MappingType == SliderMappingType.None;

    /// <summary>The apps (image names) an Apps slider controls, shown as chips.</summary>
    public ObservableCollection<AppChip> Apps { get; } = new();

    /// <summary>The app names, in chip order.</summary>
    public IReadOnlyList<string> AppNames => Apps.Select(a => a.Name).ToList();

    /// <summary>"Current default output" followed by every render device.</summary>
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
        SliderMappingType.Processes => Apps.Count > 0 ? string.Join(", ", AppNames) : "No apps yet",
        SliderMappingType.Unmapped => "All other apps",
        SliderMappingType.Device => SelectedDevice,
        _ => "Not set up",
    };

    // ── Advanced ──────────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _invert;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurveIndex))]
    private ResponseCurve _curve;

    [ObservableProperty]
    private double _deadZonePercent;

    [ObservableProperty]
    private double _smoothingFactor;

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

    /// <summary>Live volume 0..1 from the controller; display only, never saved.</summary>
    [ObservableProperty]
    private double _liveLevel;

    /// <summary>Set (false → true) whenever the controller reports this slider, so the card
    /// flashes; the view's style animation does the fading.</summary>
    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isAdvancedOpen;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    // ── Card menu ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private void MoveUp() => _owner.MoveUp(this);

    [RelayCommand]
    private void MoveDown() => _owner.MoveDown(this);

    [RelayCommand]
    private void Reset() => _owner.Reset(this);

    [RelayCommand]
    private void RemoveLast() => _owner.RemoveLast();

    /// <summary>Removes the chip for <paramref name="app"/> and saves.</summary>
    public void RemoveApp(string? app)
    {
        var existing = Apps.FirstOrDefault(a => string.Equals(a.Name, app, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return;
        Apps.Remove(existing);
        WriteApps();
    }

    // ── Construction ──────────────────────────────────────────────────────────

    internal SliderItemViewModel(SliderConfig cfg, int index, IAudioEngine audio,
                                 IReadOnlyList<AudioDevice> devices, ISliderCardOwner owner, Action save)
    {
        Config = cfg;
        _audio = audio;
        _owner = owner;
        _save = save;
        _index = index;
        SetDevices(devices);
        Reload();
    }

    /// <summary>Re-reads every field from <see cref="Config"/> without saving (after Reset or Undo).</summary>
    internal void Reload()
    {
        _loading = true;
        try
        {
            MappingType = Config.Type;
            Apps.Clear();
            foreach (var process in Config.Processes) Apps.Add(new AppChip(process, RemoveApp));
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

    /// <summary>Adds a chip (trimmed; a case-insensitive duplicate is ignored) and saves.</summary>
    public bool AddApp(string? app)
    {
        var name = app?.Trim() ?? "";
        if (name.Length == 0 || Apps.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
            return false;
        Apps.Add(new AppChip(name, RemoveApp));
        WriteApps();
        return true;
    }

    private void WriteApps()
    {
        Config.Processes = AppNames.ToList();
        OnPropertyChanged(nameof(Summary));
        _owner.AppsChanged();
        Persist();
    }

    /// <summary>Distinct image names of every live audio session, for the chip input's suggestions.</summary>
    public List<string> GetLiveProcessNames()
        => _audio.GetSessions()
            .Select(s => s.ImageName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ── Write-through ─────────────────────────────────────────────────────────

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

    /// <summary>Marks the card active again, restarting its flash.</summary>
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
    /// Re-seeds the device picker after a device was plugged in or removed. Re-asserts the current
    /// selection without saving; a device that is gone simply shows no selection.
    /// </summary>
    public void RefreshDevices(IReadOnlyList<AudioDevice> devices)
    {
        var keep = SelectedDevice;
        _loading = true;
        try
        {
            SetDevices(devices);
            SelectedDevice = keep;
        }
        finally { _loading = false; }
        OnPropertyChanged(nameof(SelectedDevice));
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

/// <summary>One app chip: its name and the command that removes it.</summary>
public sealed partial class AppChip
{
    private readonly Action<string> _remove;

    public AppChip(string name, Action<string> remove)
    {
        Name = name;
        _remove = remove;
    }

    public string Name { get; }

    [RelayCommand]
    private void Remove() => _remove(Name);

    public override string ToString() => Name;
}

/// <summary>What a slider card asks of the list that owns it.</summary>
internal interface ISliderCardOwner
{
    void MoveUp(SliderItemViewModel item);
    void MoveDown(SliderItemViewModel item);
    void Reset(SliderItemViewModel item);
    void RemoveLast();
    void AppsChanged();
}
