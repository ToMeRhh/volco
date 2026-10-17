using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Input.Actions;
using Volco.Core.Media;

namespace Volco.App.ViewModels;

/// <summary>An entry of a media action's app picker: "Current app" (no id), a live app, or the
/// saved app while it is not running.</summary>
public sealed record MediaAppChoice(string? Id, string Name, string Label)
{
    public static MediaAppChoice Current { get; } = new(null, "", "Current app (whatever plays)");

    public override string ToString() => Label;
}

/// <summary>What an action slot needs from its page beyond its own config: which custom actions
/// this platform has, the live media apps, and whether a file exists.</summary>
internal sealed record ActionSlotHost(ActionAvailability Availability, Func<IReadOnlyList<MediaApp>> MediaApps,
                                      Func<string, bool> FileExists)
{
    public static ActionSlotHost Default { get; } =
        new(ActionAvailability.All, () => Array.Empty<MediaApp>(), File.Exists);
}

/// <summary>
/// One action slot of a button (Press, Double press or Hold) for the preset being edited: the
/// inspector's row, Try, and (as a draft) the action dialog's settings. Re-pointed at another
/// preset's <see cref="ButtonActionConfig"/> with <see cref="Bind"/> when the edited preset changes.
/// </summary>
public sealed partial class ActionSlotViewModel : ObservableObject, IDeviceLabeler
{
    private readonly Action _changed;
    private readonly Func<string, DeviceChoice> _describe;
    private readonly Action<ButtonActionConfig> _execute;
    private readonly Func<IReadOnlyList<PresetChoice>> _presets;
    private readonly ActionSlotHost _host;
    private ButtonActionConfig _cfg = new();
    private bool _loading;

    /// <summary>"Press", "Double press" or "Hold".</summary>
    public string Label { get; }

    public IReadOnlyList<ActionChoice> Choices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsDevice), nameof(NeedsPair), nameof(NeedsPreset), nameof(CanTest),
                              nameof(NeedsMediaApp), nameof(NeedsCommand), nameof(NeedsWhileRunning),
                              nameof(CommandPlaceholder),
                              nameof(IconKey), nameof(IsUsb))]
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

    // ── Media ─────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private IReadOnlyList<MediaAppChoice> _mediaAppChoices = new[] { MediaAppChoice.Current };

    [ObservableProperty]
    private MediaAppChoice? _selectedMediaApp = MediaAppChoice.Current;

    // ── Programs and scripts ──────────────────────────────────────────────────

    [ObservableProperty]
    private string _commandPath = "";

    [ObservableProperty]
    private string _commandArguments = "";

    [ObservableProperty]
    private string _commandWorkingDirectory = "";

    [ObservableProperty]
    private string _commandName = "";

    /// <summary>Run a script: a press while it runs starts another run (else it is ignored).</summary>
    [ObservableProperty]
    private bool _startAnother;

    private ButtonActionType Type => SelectedChoice.Type ?? ButtonActionType.None;
    public bool NeedsDevice => ActionCatalog.NeedsDevice(Type);
    public bool NeedsPair => ActionCatalog.NeedsPair(Type);
    public bool NeedsPreset => ActionCatalog.NeedsPreset(Type);
    public bool NeedsMediaApp => ActionCatalog.NeedsMediaApp(Type);
    public bool NeedsCommand => ActionCatalog.NeedsCommand(Type);
    public bool NeedsWhileRunning => ActionCatalog.NeedsWhileRunning(Type);
    public bool CanTest => Type != ButtonActionType.None;

    // ── The Controls page's inspector row ─────────────────────────────────────

    /// <summary>The row's text: the action's short label, or "—" when the slot is empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowTip))]
    private string _rowText = "—";

    /// <summary>The first problem <see cref="Volco.Core.Input.Actions.ActionValidator"/> finds in
    /// the saved action, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string? _problem;

    /// <summary>This slot holds the button's main action (the one its light follows).</summary>
    [ObservableProperty]
    private bool _isMain;

    public bool HasProblem => Problem is not null;

    /// <summary>The action's <see cref="ButtonActionType"/> name, for its icon.</summary>
    public string IconKey => Type.ToString();

    /// <summary>Programs and scripts carry the "USB" tag, whatever the transport.</summary>
    public bool IsUsb => NeedsCommand;

    /// <summary>"Press: Mute mic", for the dense drawing's icons.</summary>
    public string RowTip => $"{Label}: {RowText}";

    /// <summary>Set by the button after each change (<see cref="Problem"/> separately).</summary>
    internal void SetRow(string text, bool isMain)
    {
        RowText = text;
        IsMain = isMain;
    }

    // ── The action dialog's validation (spec §6.4) ────────────────────────────

    private static readonly IReadOnlyDictionary<string, string> NoErrors = new Dictionary<string, string>();

    /// <summary>Builds the context <see cref="Errors"/> is checked against; null checks structure only.</summary>
    internal Func<ActionValidationContext>? Validation { get; init; }

    /// <summary>Field key (<see cref="ActionValidator"/>'s) to message; empty until <see cref="ShowErrors"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeviceError), nameof(DeviceBError), nameof(PresetError), nameof(PathError),
                              nameof(WorkingDirectoryError), nameof(FirstErrorKey))]
    private IReadOnlyDictionary<string, string> _errors = NoErrors;

    /// <summary>Set by the dialog's first Done: from then on every change re-checks <see cref="Errors"/>.</summary>
    [ObservableProperty]
    private bool _showErrors;

    public string? DeviceError => Errors.GetValueOrDefault(ActionValidator.Device);
    public string? DeviceBError => Errors.GetValueOrDefault(ActionValidator.DeviceB);
    public string? PresetError => Errors.GetValueOrDefault(ActionValidator.Preset);
    public string? PathError => Errors.GetValueOrDefault(ActionValidator.Path);
    public string? WorkingDirectoryError => Errors.GetValueOrDefault(ActionValidator.WorkingDirectory);

    /// <summary>The first field with an error, in the settings pane's order; null when valid.</summary>
    public string? FirstErrorKey
        => new[] { ActionValidator.Device, ActionValidator.DeviceB, ActionValidator.Preset, ActionValidator.Path,
                   ActionValidator.WorkingDirectory }.FirstOrDefault(Errors.ContainsKey);

    /// <summary>Checks the action now (whatever <see cref="ShowErrors"/> says) and returns the problems.</summary>
    internal IReadOnlyDictionary<string, string> Check()
    {
        var context = Validation?.Invoke() ?? new ActionValidationContext(PresetChoices.Select(p => p.Id).ToList(), _ => null, _ => true);
        var errors = ActionValidator.Validate(_cfg, context);
        Errors = errors.Count == 0 ? NoErrors : errors;
        return errors;
    }

    partial void OnShowErrorsChanged(bool value)
    {
        if (value) Check();
        else Errors = NoErrors;
    }

    /// <summary>A user edit was written to the config.</summary>
    private void Edited()
    {
        if (ShowErrors) Check();
        _changed();
    }

    public string CommandPlaceholder => Type == ButtonActionType.RunScript ? @"C:\Scripts\lights.bat" : @"C:\Games\game.exe";

    internal ActionSlotViewModel(string label, IReadOnlyList<AudioDevice> devices,
                                 Func<IReadOnlyList<PresetChoice>> presets,
                                 Action<ButtonActionConfig> execute, Action changed,
                                 Func<string, DeviceChoice> describe, ActionSlotHost? host = null)
    {
        _describe = describe;
        Label = label;
        _presets = presets;
        _execute = execute;
        _changed = changed;
        _host = host ?? ActionSlotHost.Default;
        Choices = ActionCatalog.ChoicesFor(_host.Availability);
        _selectedChoice = ActionCatalog.For(ButtonActionType.None, Choices);
        SetDevices(devices);
    }

    public DeviceChoice Describe(string friendlyName) => _describe(friendlyName);

    internal ButtonActionConfig Config => _cfg;

    /// <summary>Shows <paramref name="cfg"/> (another preset's action) without saving.</summary>
    internal void Bind(ButtonActionConfig cfg)
    {
        _cfg = cfg;
        _loading = true;
        try
        {
            SelectedChoice = ActionCatalog.For(cfg.Action, Choices);
            SetDevices(null);
            Device = cfg.Device?.FriendlyName ?? "";
            DeviceB = cfg.DeviceB?.FriendlyName ?? "";
            RefreshPresets();
            CommandPath = cfg.Command?.Path ?? "";
            CommandArguments = cfg.Command?.Arguments ?? "";
            CommandWorkingDirectory = cfg.Command?.WorkingDirectory ?? "";
            CommandName = cfg.Command?.Name ?? "";
            StartAnother = cfg.WhileRunning == WhileRunningPolicy.StartAnother;
            RefreshMediaApps(force: true);
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

    /// <summary>Re-reads the live media apps; the list is replaced only when it changed, so an
    /// open picker is not disturbed by every playback change.</summary>
    internal void RefreshMediaApps(bool force = false)
    {
        var choices = new List<MediaAppChoice> { MediaAppChoice.Current };
        foreach (var app in _host.MediaApps())
            choices.Add(new MediaAppChoice(app.Id, app.Name, app.Name));
        if (_cfg.MediaApp is { Id.Length: > 0 } saved
            && !choices.Exists(c => string.Equals(c.Id, saved.Id, StringComparison.OrdinalIgnoreCase)))
        {
            var name = saved.Name.Length > 0 ? saved.Name : saved.Id;
            choices.Add(new MediaAppChoice(saved.Id, name, $"{name} (not running)"));
        }
        if (!force && choices.SequenceEqual(MediaAppChoices)) return;

        var wasLoading = _loading;
        _loading = true;
        try
        {
            MediaAppChoices = choices;
            SelectedMediaApp = _cfg.MediaApp is { Id.Length: > 0 } target
                ? choices.First(c => string.Equals(c.Id, target.Id, StringComparison.OrdinalIgnoreCase))
                : MediaAppChoice.Current;
        }
        finally { _loading = wasLoading; }
    }

    [RelayCommand]
    private void Test() => _execute(_cfg);

    partial void OnSelectedChoiceChanged(ActionChoice value)
    {
        if (_loading) return;
        // Headers and unavailable entries cannot be chosen; snap back to the stored action.
        if (value is null || !value.IsSelectable)
        {
            _loading = true;
            try { SelectedChoice = ActionCatalog.For(_cfg.Action, Choices); }
            finally { _loading = false; }
            return;
        }
        _cfg.Action = value.Type!.Value;
        Edited();
    }

    partial void OnDeviceChanged(string value)
    {
        // A swapped ItemsSource can null the two-way-bound selection for a moment; never user intent.
        if (_loading || string.IsNullOrEmpty(value)) return;
        _cfg.Device = DeviceRefLookup.DeviceRefFromName(_devices, value);
        Edited();
    }

    partial void OnDeviceBChanged(string value)
    {
        if (_loading || string.IsNullOrEmpty(value)) return;
        _cfg.DeviceB = DeviceRefLookup.DeviceRefFromName(_devices, value);
        Edited();
    }

    partial void OnSelectedPresetChanged(PresetChoice? value)
    {
        if (_loading || value is null) return;
        _cfg.PresetId = value.Id;
        Edited();
    }

    partial void OnSelectedMediaAppChanged(MediaAppChoice? value)
    {
        if (_loading || value is null) return;
        _cfg.MediaApp = value.Id is null ? null : new MediaAppConfig { Id = value.Id, Name = value.Name };
        Edited();
    }

    partial void OnCommandPathChanged(string value)
    {
        if (_loading) return;
        Command().Path = value.Trim();
        Edited();
    }

    partial void OnCommandArgumentsChanged(string value)
    {
        if (_loading) return;
        Command().Arguments = value;
        Edited();
    }

    partial void OnCommandWorkingDirectoryChanged(string value)
    {
        if (_loading) return;
        Command().WorkingDirectory = value.Trim();
        Edited();
    }

    partial void OnCommandNameChanged(string value)
    {
        if (_loading) return;
        Command().Name = value;
        Edited();
    }

    partial void OnStartAnotherChanged(bool value)
    {
        if (_loading) return;
        _cfg.WhileRunning = value ? WhileRunningPolicy.StartAnother : WhileRunningPolicy.Ignore;
        Edited();
    }

    private CommandConfig Command() => _cfg.Command ??= new CommandConfig();

    /// <summary>Re-seeds the device pickers; keeps the current selections without saving.</summary>
    internal void RefreshDevices(IReadOnlyList<AudioDevice> devices)
    {
        var a = Device;
        var b = DeviceB;
        _loading = true;
        try
        {
            SetDevices(devices);
            // Pass through no selection, so the selection boxes re-run their label template even
            // when the selected name is unchanged (a nickname may have changed).
            Device = "";
            DeviceB = "";
            Device = a;
            DeviceB = b;
        }
        finally { _loading = false; }
        OnPropertyChanged(nameof(Device));
        OnPropertyChanged(nameof(DeviceB));
        if (ShowErrors) Check();   // a device may now be an input, or gone
    }

    private IReadOnlyList<AudioDevice> _devices = Array.Empty<AudioDevice>();

    /// <summary>The render devices the pickers list.</summary>
    internal IReadOnlyList<AudioDevice> Devices => _devices;

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
