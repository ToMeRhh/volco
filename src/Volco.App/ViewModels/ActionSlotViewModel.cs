using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Audio;
using Volco.Core.Config;

namespace Volco.App.ViewModels;

/// <summary>
/// One action row of a button card (Press, Double press or Hold) for the preset being edited:
/// the grouped action picker, that action's settings, and Test. Re-pointed at another preset's
/// <see cref="ButtonActionConfig"/> with <see cref="Bind"/> when the edited preset changes.
/// </summary>
public sealed partial class ActionSlotViewModel : ObservableObject
{
    private readonly Action _changed;
    private readonly Action<ButtonActionConfig> _execute;
    private readonly Func<IReadOnlyList<PresetChoice>> _presets;
    private ButtonActionConfig _cfg = new();
    private bool _loading;

    /// <summary>"Press", "Double press" or "Hold".</summary>
    public string Label { get; }

    public IReadOnlyList<ActionChoice> Choices => ActionCatalog.Choices;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsDevice), nameof(NeedsPair), nameof(NeedsPreset), nameof(CanTest))]
    private ActionChoice _selectedChoice = ActionCatalog.For(ButtonActionType.None);

    /// <summary>Render device names (plus a configured one that is not plugged in).</summary>
    [ObservableProperty]
    private List<string> _deviceNames = new();

    /// <summary>The device of Mute a specific device / Switch output to…, or output A of Toggle.</summary>
    [ObservableProperty]
    private string _device = "";

    /// <summary>Output B of Toggle between two outputs.</summary>
    [ObservableProperty]
    private string _deviceB = "";

    [ObservableProperty]
    private IReadOnlyList<PresetChoice> _presetChoices = Array.Empty<PresetChoice>();

    [ObservableProperty]
    private PresetChoice? _selectedPreset;

    private ButtonActionType Type => SelectedChoice.Type ?? ButtonActionType.None;
    public bool NeedsDevice => ActionCatalog.NeedsDevice(Type);
    public bool NeedsPair => ActionCatalog.NeedsPair(Type);
    public bool NeedsPreset => ActionCatalog.NeedsPreset(Type);
    public bool CanTest => Type != ButtonActionType.None;

    internal ActionSlotViewModel(string label, IReadOnlyList<AudioDevice> devices,
                                 Func<IReadOnlyList<PresetChoice>> presets,
                                 Action<ButtonActionConfig> execute, Action changed)
    {
        Label = label;
        _presets = presets;
        _execute = execute;
        _changed = changed;
        SetDevices(devices);
    }

    internal ButtonActionConfig Config => _cfg;

    /// <summary>Shows <paramref name="cfg"/> (another preset's action) without saving.</summary>
    internal void Bind(ButtonActionConfig cfg)
    {
        _cfg = cfg;
        _loading = true;
        try
        {
            SelectedChoice = ActionCatalog.For(cfg.Action);
            SetDevices(null);
            Device = cfg.Device?.FriendlyName ?? "";
            DeviceB = cfg.DeviceB?.FriendlyName ?? "";
            RefreshPresets();
        }
        finally { _loading = false; }
    }

    /// <summary>Re-reads the preset list (a preset was added, renamed or deleted).</summary>
    internal void RefreshPresets()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            PresetChoices = _presets();
            SelectedPreset = PresetChoices.FirstOrDefault(p => p.Id == _cfg.PresetId);
        }
        finally { _loading = wasLoading; }
    }

    [RelayCommand]
    private void Test() => _execute(_cfg);

    partial void OnSelectedChoiceChanged(ActionChoice value)
    {
        if (_loading) return;
        // Headers and "coming later" entries cannot be chosen; snap back to the stored action.
        if (value is null || !value.IsSelectable)
        {
            _loading = true;
            try { SelectedChoice = ActionCatalog.For(_cfg.Action); }
            finally { _loading = false; }
            return;
        }
        _cfg.Action = value.Type!.Value;
        _changed();
    }

    partial void OnDeviceChanged(string value)
    {
        // A swapped ItemsSource can null the two-way-bound selection for a moment; never user intent.
        if (_loading || string.IsNullOrEmpty(value)) return;
        _cfg.Device = DeviceRefLookup.DeviceRefFromName(value);
        _changed();
    }

    partial void OnDeviceBChanged(string value)
    {
        if (_loading || string.IsNullOrEmpty(value)) return;
        _cfg.DeviceB = DeviceRefLookup.DeviceRefFromName(value);
        _changed();
    }

    partial void OnSelectedPresetChanged(PresetChoice? value)
    {
        if (_loading || value is null) return;
        _cfg.PresetId = value.Id;
        _changed();
    }

    /// <summary>Re-seeds the device pickers; keeps the current selections without saving.</summary>
    internal void RefreshDevices(IReadOnlyList<AudioDevice> devices)
    {
        var a = Device;
        var b = DeviceB;
        _loading = true;
        try
        {
            SetDevices(devices);
            Device = a;
            DeviceB = b;
        }
        finally { _loading = false; }
        OnPropertyChanged(nameof(Device));
        OnPropertyChanged(nameof(DeviceB));
    }

    private IReadOnlyList<AudioDevice> _devices = Array.Empty<AudioDevice>();

    private void SetDevices(IReadOnlyList<AudioDevice>? devices)
    {
        if (devices is not null) _devices = devices;
        var names = _devices.Select(device => device.FriendlyName).ToList();
        // Friendly-name-only devices that are not plugged in still show as selected.
        foreach (var configured in new[] { _cfg.Device, _cfg.DeviceB })
            if (configured is { FriendlyName.Length: > 0 }
                && !names.Contains(configured.FriendlyName, StringComparer.OrdinalIgnoreCase))
                names.Add(configured.FriendlyName);
        DeviceNames = names;
    }
}
