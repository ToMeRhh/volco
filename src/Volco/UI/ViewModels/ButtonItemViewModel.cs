using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Input.Actions;
using static Volco.UI.ViewModels.DeviceRefLookup;

namespace Volco.UI.ViewModels;

/// <summary>
/// Lightweight model for a single press-action slot (single/double/long).
/// </summary>
public partial class PressActionViewModel : ObservableObject
{
    private readonly ButtonActionConfig _cfg;
    private readonly Action _save;
    private readonly IAudioEngine _audio;
    private readonly ConfigManager _config;

    [ObservableProperty]
    private ButtonActionType _actionType;

    [ObservableProperty]
    private string _deviceId = "";

    // ── Derived ────────────────────────────────────────────────────────────────

    /// <summary>True when the selected action requires a device selection.</summary>
    public bool NeedsDevice =>
        ActionType is ButtonActionType.MuteSpecificDevice or ButtonActionType.SetOutputDevice;

    /// <summary>
    /// Zero-based index for the action type ComboBox (TwoWay x:Bind to SelectedIndex).
    /// Order matches XAML items and ButtonActionType enum declaration order:
    /// 0=None, 1=MuteSpecificDevice, 2=MuteCurrentOutput, 3=MuteCurrentInput,
    /// 4=ToggleOutputDevice, 5=SetOutputDevice.
    /// </summary>
    public int ActionTypeIndex
    {
        get => ActionType switch
        {
            ButtonActionType.None               => 0,
            ButtonActionType.MuteSpecificDevice => 1,
            ButtonActionType.MuteCurrentOutput  => 2,
            ButtonActionType.MuteCurrentInput   => 3,
            ButtonActionType.ToggleOutputDevice => 4,
            ButtonActionType.SetOutputDevice    => 5,
            _                                   => 0,
        };
        set => ActionType = value switch
        {
            0 => ButtonActionType.None,
            1 => ButtonActionType.MuteSpecificDevice,
            2 => ButtonActionType.MuteCurrentOutput,
            3 => ButtonActionType.MuteCurrentInput,
            4 => ButtonActionType.ToggleOutputDevice,
            5 => ButtonActionType.SetOutputDevice,
            _ => ButtonActionType.None,
        };
    }

    public PressActionViewModel(ButtonActionConfig cfg, Action save, IAudioEngine audio, ConfigManager config)
    {
        _cfg    = cfg;
        _save   = save;
        _audio  = audio;
        _config = config;
        _actionType = cfg.Action;
        _deviceId   = cfg.Device?.FriendlyName ?? "";
    }

    partial void OnActionTypeChanged(ButtonActionType value)
    {
        _cfg.Action = value;
        OnPropertyChanged(nameof(NeedsDevice));
        OnPropertyChanged(nameof(ActionTypeIndex));
        _save();
    }

    partial void OnDeviceIdChanged(string value)
    {
        // Same trap as SliderItemViewModel.OnDeviceNameChanged (Bug 3 refresh) and Bug 2's
        // ComPort refresh: an ItemsSource swap can transiently null this two-way-bound
        // SelectedItem. There's no blank/"None" entry in this picker, so ignore it.
        if (string.IsNullOrEmpty(value)) return;
        _cfg.Device = DeviceRefFromName(value);
        _save();
    }

    /// <summary>
    /// Re-raises PropertyChanged for DeviceId only, so the ComboBox re-reads the still-correct
    /// backing value and re-selects it against a freshly refreshed ItemsSource (see
    /// ButtonItemViewModel.RefreshDevices). Does not touch _cfg or persist.
    /// </summary>
    public void ReassertDeviceSelection() => OnPropertyChanged(nameof(DeviceId));

    /// <summary>
    /// Builds and immediately executes the action for this press slot.
    /// No-ops when the action is None.
    /// </summary>
    [RelayCommand]
    private void Test()
    {
        ActionFactory.Create(_cfg, _audio, _config)?.Execute();
    }
}

/// <summary>
/// ViewModel for a single button entry in the Buttons settings tab.
/// Exposes the three press actions, timing parameters, and available output devices.
/// </summary>
public partial class ButtonItemViewModel : ObservableObject
{
    private readonly ButtonConfig _cfg;
    private readonly Action _save;

    // ── Press action slots ─────────────────────────────────────────────────────

    public PressActionViewModel SinglePress { get; }
    public PressActionViewModel DoublePress { get; }
    public PressActionViewModel LongPress   { get; }

    // ── Timing parameters ─────────────────────────────────────────────────────

    [ObservableProperty]
    private int _longPressMs;

    [ObservableProperty]
    private int _doublePressWindowMs;

    // ── Meta ──────────────────────────────────────────────────────────────────

    public int Index { get; }

    [ObservableProperty]
    private bool _isExpanded = true;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>Available output devices for device-picker combos.</summary>
    public ObservableCollection<AudioDevice> OutputDevices { get; } = new();

    /// <summary>
    /// Output device names for binding to ComboBox.ItemsSource (string list).
    /// Mirrors <see cref="OutputDevices"/> as plain strings so SelectedItem can
    /// bind directly to <see cref="PressActionViewModel.DeviceId"/>.
    /// </summary>
    public List<string> OutputDeviceNames { get; private set; } = new();

    // ── Constructor ───────────────────────────────────────────────────────────

    public ButtonItemViewModel(ButtonConfig cfg, IAudioEngine audio, IReadOnlyList<AudioDevice> outputDevices, ConfigManager config, Action save)
    {
        _cfg  = cfg;
        _save = save;

        Index             = cfg.Index;
        _longPressMs      = cfg.LongPressMs;
        _doublePressWindowMs = cfg.DoublePressWindowMs;

        SinglePress = new PressActionViewModel(cfg.SinglePress, save, audio, config);
        DoublePress = new PressActionViewModel(cfg.DoublePress, save, audio, config);
        LongPress   = new PressActionViewModel(cfg.LongPress,  save, audio, config);

        foreach (var d in outputDevices)
            OutputDevices.Add(d);

        OutputDeviceNames = OutputDevices.Select(d => d.FriendlyName).ToList();
    }

    // ── Timing change hooks ───────────────────────────────────────────────────

    partial void OnLongPressMsChanged(int value)
    {
        _cfg.LongPressMs = value;
        _save();
    }

    partial void OnDoublePressWindowMsChanged(int value)
    {
        _cfg.DoublePressWindowMs = value;
        _save();
    }

    /// <summary>
    /// Re-seeds the shared device picker for all three press slots from a fresh device snapshot
    /// (Bug 3). Callers must already be on the UI thread — see SliderItemViewModel.RefreshDevices
    /// for the same note; <see cref="IAudioEngine.DevicesChanged"/> fires on the audio thread and
    /// is marshalled once, in SettingsViewModel.RefreshDevices.
    /// </summary>
    public void RefreshDevices(IReadOnlyList<AudioDevice> devices)
    {
        OutputDevices.Clear();
        foreach (var d in devices)
            OutputDevices.Add(d);
        OutputDeviceNames = OutputDevices.Select(d => d.FriendlyName).ToList();
        OnPropertyChanged(nameof(OutputDeviceNames));

        SinglePress.ReassertDeviceSelection();
        DoublePress.ReassertDeviceSelection();
        LongPress.ReassertDeviceSelection();
    }
}
