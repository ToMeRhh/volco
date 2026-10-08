using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Media;

namespace Volco.App.ViewModels;

/// <summary>An entry of a media action's app picker: "Current app" (no id), a live app, or the
/// saved app while it is not running.</summary>
public sealed record MediaAppChoice(string? Id, string Name, string Label)
{
    public static MediaAppChoice Current { get; } = new(null, "", "Current app (whatever plays)");

    public override string ToString() => Label;
}

/// <summary>What an action row needs from its page beyond its own config: which custom actions
/// this platform has, the live media apps, whether presses arrive over the network (programs and
/// scripts then do not run), and whether a file exists.</summary>
internal sealed record ActionSlotHost(ActionAvailability Availability, Func<IReadOnlyList<MediaApp>> MediaApps,
                                      Func<bool> PressesOverNetwork, Func<string, bool> FileExists)
{
    public static ActionSlotHost Default { get; } =
        new(ActionAvailability.All, () => Array.Empty<MediaApp>(), () => false, File.Exists);
}

/// <summary>
/// One action row of a button card (Press, Double press or Hold) for the preset being edited:
/// the grouped action picker, that action's settings, and Test. Re-pointed at another preset's
/// <see cref="ButtonActionConfig"/> with <see cref="Bind"/> when the edited preset changes.
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
                              nameof(CommandHint), nameof(CommandPlaceholder), nameof(ShowUsbOnly))]
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

    /// <summary>The chosen file does not exist (URLs are never checked).</summary>
    [ObservableProperty]
    private bool _pathMissing;

    private ButtonActionType Type => SelectedChoice.Type ?? ButtonActionType.None;
    public bool NeedsDevice => ActionCatalog.NeedsDevice(Type);
    public bool NeedsPair => ActionCatalog.NeedsPair(Type);
    public bool NeedsPreset => ActionCatalog.NeedsPreset(Type);
    public bool NeedsMediaApp => ActionCatalog.NeedsMediaApp(Type);
    public bool NeedsCommand => ActionCatalog.NeedsCommand(Type);
    public bool NeedsWhileRunning => ActionCatalog.NeedsWhileRunning(Type);
    public bool CanTest => Type != ButtonActionType.None;

    /// <summary>Programs and scripts run only for presses over USB (custom-actions spec §7).</summary>
    public bool ShowUsbOnly => NeedsCommand && _host.PressesOverNetwork();

    public string CommandHint => Type == ButtonActionType.RunScript
        ? "Runs hidden. .bat, .cmd and .exe start fastest; PowerShell takes a moment longer. " +
          "The script gets VOLCO_BUTTON, VOLCO_GESTURE and VOLCO_PRESET."
        : "An .exe, a shortcut, a document, or a link such as steam://rungameid/570. " +
          "If the program already runs, its window comes to the front.";

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
            UpdatePathMissing();
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

    /// <summary>The connection type changed: the "Runs only over USB" note follows.</summary>
    internal void RefreshTransport() => OnPropertyChanged(nameof(ShowUsbOnly));

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
        _changed();
    }

    partial void OnDeviceChanged(string value)
    {
        // A swapped ItemsSource can null the two-way-bound selection for a moment; never user intent.
        if (_loading || string.IsNullOrEmpty(value)) return;
        _cfg.Device = DeviceRefLookup.DeviceRefFromName(_devices, value);
        _changed();
    }

    partial void OnDeviceBChanged(string value)
    {
        if (_loading || string.IsNullOrEmpty(value)) return;
        _cfg.DeviceB = DeviceRefLookup.DeviceRefFromName(_devices, value);
        _changed();
    }

    partial void OnSelectedPresetChanged(PresetChoice? value)
    {
        if (_loading || value is null) return;
        _cfg.PresetId = value.Id;
        _changed();
    }

    partial void OnSelectedMediaAppChanged(MediaAppChoice? value)
    {
        if (_loading || value is null) return;
        _cfg.MediaApp = value.Id is null ? null : new MediaAppConfig { Id = value.Id, Name = value.Name };
        _changed();
    }

    partial void OnCommandPathChanged(string value)
    {
        UpdatePathMissing();
        if (_loading) return;
        Command().Path = value.Trim();
        _changed();
    }

    partial void OnCommandArgumentsChanged(string value)
    {
        if (_loading) return;
        Command().Arguments = value;
        _changed();
    }

    partial void OnCommandWorkingDirectoryChanged(string value)
    {
        if (_loading) return;
        Command().WorkingDirectory = value.Trim();
        _changed();
    }

    partial void OnCommandNameChanged(string value)
    {
        if (_loading) return;
        Command().Name = value;
        _changed();
    }

    partial void OnStartAnotherChanged(bool value)
    {
        if (_loading) return;
        _cfg.WhileRunning = value ? WhileRunningPolicy.StartAnother : WhileRunningPolicy.Ignore;
        _changed();
    }

    private CommandConfig Command() => _cfg.Command ??= new CommandConfig();

    private void UpdatePathMissing()
    {
        var path = CommandPath.Trim();
        PathMissing = path.Length > 0 && !Volco.Core.Programs.CommandPath.IsUrl(path) && !_host.FileExists(path);
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
