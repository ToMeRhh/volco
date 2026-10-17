using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.App.ViewModels.Search;
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Devices;
using Volco.Core.Programs;

namespace Volco.App.ViewModels;

/// <summary>What a row of the target dialog sets: the main output, a device, an app, "Other app…"
/// (a file picker) or everything else.</summary>
public enum TargetKind { Main, Device, App, OtherApp, Rest }

/// <summary>One row of the target dialog. <see cref="OwnedBy"/> is the name of another slider that
/// has the app; <see cref="Highlight"/> holds the positions in <see cref="Name"/> that matched.</summary>
public sealed record TargetItem(string Key, string Name, string Secondary, TargetKind Kind, bool IsOn, string? OwnedBy,
                                IReadOnlyList<int> Highlight)
{
    /// <summary>Where Up and Down are; Enter toggles it.</summary>
    public bool IsCurrent { get; init; }

    /// <summary>The row's icon (a resource key).</summary>
    public string IconKey { get; init; } = "Icon.Plus";

    public bool IsRadio => Kind is TargetKind.Main or TargetKind.Device or TargetKind.Rest;
    public bool IsCheck => Kind == TargetKind.App;
    public bool HasMark => IsRadio || IsCheck;
    public bool HasSecondary => Secondary.Length > 0;
    public bool HasOwner => OwnedBy is not null;
}

/// <summary>A section of the target dialog. While searching it is open whatever its remembered
/// state, holds only its matches by score, and shows <see cref="MatchCount"/>.</summary>
public sealed record TargetSection(string Name, bool IsCollapsed, IReadOnlyList<TargetItem> Items, int MatchCount)
{
    public bool IsSearching { get; init; }

    public bool IsOpen => !IsCollapsed;
}

/// <summary>
/// The target dialog (spec docs/design/controls-page.md §6.2, §6.3, §6.4): what a slider controls.
/// Devices (the main output or one device) and Everything else are single choices; apps are ticked,
/// several at once. The rows edit a draft; Done validates it and writes it through
/// <see cref="SlidersViewModel"/>, moving ticked apps off other sliders and turning another
/// Everything else slider into an empty apps one. Cancel, Escape or a click outside write nothing.
/// </summary>
public sealed partial class TargetDialogViewModel : ObservableObject, ISearchList, IDisposable
{
    private const string Devices = "Devices";
    private const string Apps = "Apps";
    private const string Other = "Other";

    private readonly SliderItemViewModel _slider;
    private readonly SlidersViewModel _sliders;
    private readonly Dictionary<string, bool> _collapsed;
    private readonly Action _close;
    private readonly SliderConfig _original;
    private IReadOnlyList<AudioDevice> _devices;

    // The draft.
    private SliderMappingType _type;
    private DeviceRefConfig? _device;
    private readonly List<string> _apps;

    // The device each device row stands for, from the last rebuild.
    private readonly Dictionary<string, DeviceRefConfig> _deviceRows = new();
    private string? _current;
    private string? _best;
    private bool _tried;

    /// <summary>"Slider {N} controls".</summary>
    public string Title { get; }

    public SliderItemViewModel Slider => _slider;

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoMatches), nameof(NoMatchesText))]
    private IReadOnlyList<TargetSection> _sections = Array.Empty<TargetSection>();

    /// <summary>Why Done was refused; after the first Done it follows every change.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => Error is not null;

    /// <summary>The rows shown, in list order (the open sections' rows).</summary>
    public IReadOnlyList<TargetItem> VisibleItems => Sections.Where(s => !s.IsCollapsed).SelectMany(s => s.Items).ToList();

    public bool HasNoMatches => Sections.Count == 0;

    public string NoMatchesText => $"Nothing matches “{Query.Trim()}”.";

    /// <summary>"Other app…": picks an .exe and returns its path, or null. The view sets it to its
    /// file picker; tests set their own.</summary>
    public Func<Task<string?>>? PickExe { get; set; }

    internal TargetDialogViewModel(SliderItemViewModel slider, SlidersViewModel sliders,
                                   Dictionary<string, bool> collapsed, Action close)
    {
        _slider = slider;
        _sliders = sliders;
        _collapsed = collapsed;
        _close = close;
        _original = SlidersViewModel.Clone(slider.Config);
        _type = _original.Type;
        _device = _original.Device;
        _apps = _type == SliderMappingType.Processes ? new List<string>(_original.Processes) : new List<string>();
        _devices = sliders.Devices;
        Title = $"Slider {slider.Index + 1} controls";
        _sliders.DevicesRefreshed += OnDevicesRefreshed;
        Rebuild();
    }

    private static string Key(string section) => $"target:{section}";

    private static bool SameApp(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The slider still exists (else the dialog has nothing to write to).</summary>
    internal bool IsStillValid() => _sliders.Items.Contains(_slider);

    // ── The list ──────────────────────────────────────────────────────────────

    private sealed record Entry(TargetItem Item, string Extra);

    partial void OnQueryChanged(string value)
    {
        _current = null;
        Rebuild();
        // The best match is where Enter goes; it may be in a lower section (sections keep their order).
        if (Query.Trim().Length > 0) _current = _best;
        Rebuild();
    }

    private IEnumerable<Entry> DeviceEntries()
    {
        var config = _sliders.AppConfig;
        var audio = _sliders.Audio;
        _deviceRows.Clear();

        var main = audio.GetDefaultDevice(AudioFlow.Render) is { } d ? DeviceDisplay.For(config, d).Name : "";
        yield return new Entry(new TargetItem("main", "Main output", main.Length > 0 ? $"Now {main}" : "", TargetKind.Main,
                                              _type == SliderMappingType.Device && _device is null, null, [])
                               { IconKey = "Icon.Speaker" }, $"default {main}");

        var rows = _devices.Select(device => (Ref: new DeviceRefConfig { Id = device.Id, FriendlyName = device.FriendlyName },
                                              Look: DeviceDisplay.For(config, device)))
            .ToList();
        // A saved device that is not plugged in keeps its row.
        if (_original.Type == SliderMappingType.Device && _original.Device is { } saved && !rows.Exists(r => Matches(saved, r.Ref))
            && DeviceDisplay.For(config, audio, saved) is { } look)
            rows.Add((saved, look));
        foreach (var (reference, look) in rows)
        {
            var key = $"device:{(reference.Id.Length > 0 ? reference.Id : reference.FriendlyName)}";
            _deviceRows[key] = reference;
            var secondary = look.Name == reference.FriendlyName ? "" : reference.FriendlyName;
            var on = _type == SliderMappingType.Device && _device is not null && Matches(_device, reference);
            yield return new Entry(new TargetItem(key, look.Name, secondary, TargetKind.Device, on, null, [])
                                   { IconKey = $"Icon.Device.{look.Icon}" }, reference.FriendlyName);
        }
    }

    /// <summary>The same device: by id when the saved reference has one, else by Windows name.</summary>
    private static bool Matches(DeviceRefConfig saved, DeviceRefConfig device)
        => !string.IsNullOrEmpty(saved.Id)
            ? string.Equals(saved.Id, device.Id, StringComparison.OrdinalIgnoreCase)
            : string.Equals(saved.FriendlyName, device.FriendlyName, StringComparison.OrdinalIgnoreCase);

    private IEnumerable<Entry> AppEntries()
    {
        var live = _sliders.Audio.GetSessions()
            .Where(s => !s.IsSystemSounds && !string.IsNullOrEmpty(s.ImageName))
            .Select(s => s.ImageName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Then the apps saved on any slider (or ticked here) that aren't running.
        var saved = _apps.Concat(_sliders.Items.SelectMany(i => i.Config.Type == SliderMappingType.Processes ? i.Config.Processes : Enumerable.Empty<string>()))
            .Where(a => a.Trim().Length > 0 && !live.Contains(a, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var (app, running) in live.Select(a => (a, true)).Concat(saved.Select(a => (a, false))))
        {
            var on = _type == SliderMappingType.Processes && _apps.Exists(a => SameApp(a, app));
            var owner = on ? null : _sliders.Items.FirstOrDefault(i => i != _slider && i.Config.Type == SliderMappingType.Processes
                                                                          && i.Config.Processes.Exists(p => SameApp(p, app)))?.Label;
            yield return new Entry(new TargetItem($"app:{app}", CommandPath.FileNameWithoutExtension(app),
                                                  running ? app : "Not running", TargetKind.App, on, owner, [])
                                   { IconKey = "Icon.Device.App" }, app);
        }
        yield return new Entry(new TargetItem("other-app", "Other app…", "", TargetKind.OtherApp, false, null, [])
                               { IconKey = "Icon.Plus" }, "browse exe");
    }

    private IEnumerable<Entry> OtherEntries()
    {
        yield return new Entry(new TargetItem("rest", "Everything else", "", TargetKind.Rest,
                                              _type == SliderMappingType.Unmapped, null, [])
                               { IconKey = "Icon.Volume" }, "unmapped rest other apps");
    }

    private void Rebuild()
    {
        var query = Query.Trim();
        var searching = query.Length > 0;
        var sections = new List<TargetSection>();
        var bestScore = double.MinValue;
        _best = null;
        foreach (var (name, entries) in new[] { (Devices, DeviceEntries()), (Apps, AppEntries()), (Other, OtherEntries()) })
        {
            var matches = new List<(TargetItem Item, double Score)>();
            foreach (var entry in entries.ToList())
            {
                if (!searching) matches.Add((entry.Item, 0));
                else if (FuzzyMatcher.MatchItem(query, entry.Item.Name, entry.Extra) is { } match)
                {
                    matches.Add((entry.Item with { Highlight = match.Indexes }, match.Score));
                    if (match.Score > bestScore) (bestScore, _best) = (match.Score, entry.Item.Key);
                }
            }
            if (matches.Count == 0) continue;
            if (searching) matches = matches.OrderByDescending(m => m.Score).ToList();   // stable
            var items = matches.Select(m => m.Item with { IsCurrent = m.Item.Key == _current }).ToList();
            var collapsed = !searching && _collapsed.GetValueOrDefault(Key(name));
            sections.Add(new TargetSection(name, collapsed, items, items.Count) { IsSearching = searching });
        }
        Sections = sections;
        OnPropertyChanged(nameof(VisibleItems));
    }

    /// <summary>Opens or closes a section and remembers it for this app run. While searching every
    /// section with matches stays open, and the remembered state is left alone.</summary>
    [RelayCommand]
    private void ToggleSection(string name)
    {
        if (Query.Trim().Length > 0) return;
        _collapsed[Key(name)] = !_collapsed.GetValueOrDefault(Key(name));
        Rebuild();
    }

    /// <summary>Down (+1) and Up (−1) through the shown rows.</summary>
    public void MoveSelection(int step)
    {
        var items = VisibleItems;
        if (items.Count == 0) return;
        var at = items.ToList().FindIndex(i => i.Key == _current);
        var next = at < 0 ? (step > 0 ? 0 : items.Count - 1) : Math.Clamp(at + step, 0, items.Count - 1);
        _current = items[next].Key;
        Rebuild();
    }

    /// <summary>Enter: toggles the current row (the top result after typing).</summary>
    public void PickCurrent()
    {
        var item = VisibleItems.FirstOrDefault(i => i.Key == _current) ?? VisibleItems.FirstOrDefault();
        if (item is not null) Toggle(item);
    }

    /// <summary>A row was clicked: a device, the main output or Everything else becomes the
    /// choice; an app is ticked or unticked (the first tick replaces any other choice).</summary>
    [RelayCommand]
    public void Toggle(TargetItem item)
    {
        _current = item.Key;
        switch (item.Kind)
        {
            case TargetKind.Main:
                _type = SliderMappingType.Device;
                _device = null;
                break;
            case TargetKind.Device:
                if (!_deviceRows.TryGetValue(item.Key, out var device)) return;
                _type = SliderMappingType.Device;
                _device = new DeviceRefConfig { Id = device.Id, FriendlyName = device.FriendlyName };
                break;
            case TargetKind.App:
                ToggleApp(item.Key["app:".Length..]);
                break;
            case TargetKind.Rest:
                _type = SliderMappingType.Unmapped;
                break;
            case TargetKind.OtherApp:
                _ = AddOtherAppAsync();
                return;
        }
        Changed();
    }

    private void ToggleApp(string app)
    {
        if (_type != SliderMappingType.Processes)
        {
            _type = SliderMappingType.Processes;
            _apps.Clear();
        }
        var at = _apps.FindIndex(a => SameApp(a, app));
        if (at >= 0) _apps.RemoveAt(at);
        else _apps.Add(app);
    }

    /// <summary>"Other app…": the picked exe's file name is ticked.</summary>
    private async Task AddOtherAppAsync()
    {
        if (PickExe is not { } pick || await pick() is not { } path) return;
        var app = CommandPath.FileName(path);
        if (app.Length == 0) return;
        if (_type != SliderMappingType.Processes || !_apps.Exists(a => SameApp(a, app))) ToggleApp(app);
        _current = $"app:{app}";
        Changed();
    }

    private void Changed()
    {
        if (_tried) Error = SliderTargetValidator.Validate(Draft());
        Rebuild();
    }

    private SliderConfig Draft() => new()
    {
        Type = _type,
        Device = _type == SliderMappingType.Device ? _device : null,
        Processes = _type == SliderMappingType.Processes ? new List<string>(_apps) : new List<string>(),
    };

    private bool Unchanged()
    {
        if (_type != _original.Type) return false;
        return _type switch
        {
            SliderMappingType.Device => _device is null ? _original.Device is null
                                        : _original.Device is not null && Matches(_original.Device, _device),
            SliderMappingType.Processes => _apps.SequenceEqual(_original.Processes, StringComparer.OrdinalIgnoreCase),
            _ => true,
        };
    }

    // ── Footer ────────────────────────────────────────────────────────────────

    /// <summary>Validates the draft and writes it. False, with <see cref="Error"/> set, when it is
    /// invalid. An unchanged target just closes.</summary>
    public bool Done()
    {
        if (!IsStillValid()) { _close(); return false; }
        if (Unchanged()) { _close(); return true; }
        _tried = true;
        var draft = Draft();
        Error = SliderTargetValidator.Validate(draft);
        if (Error is not null) return false;
        _sliders.SetTarget(_slider, draft);
        _close();
        return true;
    }

    [RelayCommand]
    public void Cancel() => _close();

    private void OnDevicesRefreshed(IReadOnlyList<AudioDevice> devices)
    {
        _devices = devices;
        Rebuild();
    }

    public void Dispose() => _sliders.DevicesRefreshed -= OnDevicesRefreshed;
}
