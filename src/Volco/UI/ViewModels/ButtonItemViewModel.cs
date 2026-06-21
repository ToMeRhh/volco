using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Audio;
using Volco.Config;
using Volco.Input.Actions;

namespace Volco.UI.ViewModels;

/// <summary>
/// Identifies which of the three press kinds to operate on.
/// </summary>
public enum PressKind { Single, Double, Long }

/// <summary>
/// Lightweight model for a single press-action slot (single/double/long).
/// </summary>
public partial class PressActionViewModel : ObservableObject
{
    private readonly ButtonActionConfig _cfg;
    private readonly Action _save;

    [ObservableProperty]
    private ButtonActionType _actionType;

    [ObservableProperty]
    private string _deviceId = "";

    // ── Derived ────────────────────────────────────────────────────────────────

    /// <summary>True when the selected action requires a device selection.</summary>
    public bool NeedsDevice =>
        ActionType is ButtonActionType.MuteSpecificDevice or ButtonActionType.SetOutputDevice;

    public PressActionViewModel(ButtonActionConfig cfg, Action save)
    {
        _cfg = cfg;
        _save = save;
        _actionType = cfg.Action;
        _deviceId = cfg.DeviceId ?? "";
    }

    partial void OnActionTypeChanged(ButtonActionType value)
    {
        _cfg.Action = value;
        OnPropertyChanged(nameof(NeedsDevice));
        _save();
    }

    partial void OnDeviceIdChanged(string value)
    {
        _cfg.DeviceId = string.IsNullOrEmpty(value) ? null : value;
        _save();
    }
}

/// <summary>
/// ViewModel for a single button entry in the Buttons settings tab.
/// Exposes the three press actions, timing parameters, available output devices,
/// and a Test command that executes the selected action via <see cref="ActionFactory"/>.
/// </summary>
public partial class ButtonItemViewModel : ObservableObject
{
    private readonly ButtonConfig _cfg;
    private readonly IAudioManager _audio;
    private readonly ConfigManager _config;
    private readonly Action _save;

    // ── Press action slots ─────────────────────────────────────────────────────

    public PressActionViewModel SinglePress { get; }
    public PressActionViewModel DoublePress { get; }
    public PressActionViewModel LongPress { get; }

    // ── Timing parameters ─────────────────────────────────────────────────────

    [ObservableProperty]
    private int _longPressMs;

    [ObservableProperty]
    private int _doublePressWindowMs;

    // ── Meta ──────────────────────────────────────────────────────────────────

    public int Index { get; }

    /// <summary>Available output devices for device-picker combos.</summary>
    public ObservableCollection<AudioDeviceInfo> OutputDevices { get; } = new();

    // ── Constructor ───────────────────────────────────────────────────────────

    public ButtonItemViewModel(ButtonConfig cfg, IAudioManager audio, ConfigManager config, Action save)
    {
        _cfg = cfg;
        _audio = audio;
        _config = config;
        _save = save;

        Index = cfg.Index;
        _longPressMs = cfg.LongPressMs;
        _doublePressWindowMs = cfg.DoublePressWindowMs;

        SinglePress = new PressActionViewModel(cfg.SinglePress, save);
        DoublePress = new PressActionViewModel(cfg.DoublePress, save);
        LongPress  = new PressActionViewModel(cfg.LongPress,  save);

        foreach (var d in audio.GetOutputDevices())
            OutputDevices.Add(d);
    }

    // ── Timing change hooks ───────────────────────────────────────────────────

    // CommunityToolkit generates the partial hook named after the property:
    // field _longPressMs → property LongPressMs → hook OnLongPressMsChanged
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

    // ── Test command ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds and immediately executes the action for the given press kind.
    /// Safe to call from the UI; no-ops when the action is None.
    /// </summary>
    [RelayCommand]
    private void TestPress(PressKind pressKind)
    {
        var actionCfg = pressKind switch
        {
            PressKind.Single => _cfg.SinglePress,
            PressKind.Double => _cfg.DoublePress,
            PressKind.Long   => _cfg.LongPress,
            _                => _cfg.SinglePress
        };

        var action = ActionFactory.Create(actionCfg, _audio, _config);
        action?.Execute();
    }
}
