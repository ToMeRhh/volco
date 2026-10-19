using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.App.Platform;
using Volco.App.ViewModels.Search;
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Devices;
using Volco.Core.Diagnostics;
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

    /// <summary>An app row's icon, once loaded; else it shows <see cref="Letter"/>.</summary>
    public Bitmap? AppIcon { get; init; }

    /// <summary>The first letter of the name, upper-cased (the app row's tile without an icon).</summary>
    public string Letter { get; init; } = "";

    /// <summary>The live level of a running app; null for any other row.</summary>
    public AppMeterViewModel? Meter { get; init; }

    /// <summary>A device row's flow (an input is listed after the outputs).</summary>
    public AudioFlow Flow { get; init; } = AudioFlow.Render;

    /// <summary>A device row: it has the device menu (Rename, Icon).</summary>
    public bool IsDevice => Kind == TargetKind.Device;

    public bool ShowGlyph => Kind != TargetKind.App;
    public bool ShowAppIcon => Kind == TargetKind.App && AppIcon is not null;
    public bool ShowLetter => Kind == TargetKind.App && AppIcon is null;
    public bool HasMeter => Meter is not null;

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
public sealed partial class TargetDialogViewModel : ObservableObject, ISearchList, IDisposable, IDeviceNamesListener
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
    private readonly IAppIcons? _icons;
    private bool _disposed;

    // Per app key (the image name, any case): its meter, which survives rebuilds, and the icons
    // loaded so far by icon source (null = failed).
    private readonly Dictionary<string, AppMeterViewModel> _meters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Bitmap?> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _requested = new(StringComparer.OrdinalIgnoreCase);

    // The draft.
    private SliderMappingType _type;
    private DeviceRefConfig? _device;
    private readonly List<string> _apps;

    // The ticked apps' exe paths (lower-case exe name → path): the slider's, and "Other app…" picks.
    private readonly Dictionary<string, string> _paths;

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
                                   Dictionary<string, bool> collapsed, Action close, IAppIcons? icons = null)
    {
        _icons = icons;
        _slider = slider;
        _sliders = sliders;
        _collapsed = collapsed;
        _close = close;
        _original = SlidersViewModel.Clone(slider.Config);
        _type = _original.Type;
        _device = _original.Device;
        _apps = _type == SliderMappingType.Processes ? new List<string>(_original.Processes) : new List<string>();
        _paths = new Dictionary<string, string>(_original.AppPaths);
        _devices = sliders.Devices;
        Title = $"Slider {slider.Index + 1} controls";
        _sliders.DevicesRefreshed += OnDevicesRefreshed;
        _sliders.Audio.SessionsChanged += OnSessionsChanged;
        Rebuild();
        RequestIcons();
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
                                              Look: DeviceDisplay.For(config, device), device.Flow))
            .ToList();
        // A saved device that is not plugged in keeps its row, with the outputs or the inputs: its
        // flow from its settings, else from its endpoint id.
        if (_original.Type == SliderMappingType.Device && _original.Device is { } saved && !rows.Exists(r => Matches(saved, r.Ref))
            && DeviceDisplay.For(config, audio, saved) is { } savedLook)
            rows.Add((saved, savedLook, DeviceDisplay.FlowOf(config, audio, saved) ?? AudioFlow.Render));
        foreach (var (reference, look, flow) in rows.OrderBy(r => r.Flow))
        {
            var key = $"device:{(reference.Id.Length > 0 ? reference.Id : reference.FriendlyName)}";
            _deviceRows[key] = reference;
            var isInput = flow == AudioFlow.Capture;
            var windowsName = look.Name == reference.FriendlyName ? "" : reference.FriendlyName;
            var secondary = !isInput ? windowsName : windowsName.Length > 0 ? $"Input · {windowsName}" : "Input";
            var on = _type == SliderMappingType.Device && _device is not null && Matches(_device, reference);
            yield return new Entry(new TargetItem(key, look.Name, secondary, TargetKind.Device, on, null, [])
                                   {
                                       IconKey = $"Icon.Device.{look.Icon}",
                                       Flow = isInput ? AudioFlow.Capture : AudioFlow.Render,
                                   }, reference.FriendlyName);
        }
    }

    /// <summary>The same device: by id when the saved reference has one, else by Windows name.</summary>
    private static bool Matches(DeviceRefConfig saved, DeviceRefConfig device)
        => !string.IsNullOrEmpty(saved.Id)
            ? string.Equals(saved.Id, device.Id, StringComparison.OrdinalIgnoreCase)
            : string.Equals(saved.FriendlyName, device.FriendlyName, StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<AudioSessionInfo> LiveSessions()
        => _sliders.Audio.GetSessions().Where(s => !s.IsSystemSounds && !string.IsNullOrEmpty(s.ImageName)).ToList();

    private IEnumerable<Entry> AppEntries()
    {
        var sessions = LiveSessions();
        var live = sessions.Select(s => s.ImageName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
            // A running app is named by its first session that has a display name; the exe name
            // is the fallback (an app Volco cannot open) and a saved app's name.
            var appSessions = running ? sessions.Where(s => SameApp(s.ImageName, app)).ToList() : [];
            var display = appSessions.Select(s => s.DisplayName).FirstOrDefault(n => !string.IsNullOrEmpty(n));
            var name = display ?? CommandPath.FileNameWithoutExtension(app);
            var iconSource = appSessions.Select(s => s.IconPath).FirstOrDefault(p => !string.IsNullOrEmpty(p))
                             ?? SavedPath(app);
            var icon = iconSource is not null && _loaded.TryGetValue(iconSource, out var bitmap) ? bitmap : null;
            AppMeterViewModel? meter = null;
            if (running && !_meters.TryGetValue(app, out meter)) _meters[app] = meter = new AppMeterViewModel();
            yield return new Entry(new TargetItem($"app:{app}", name, running ? app : "Not running", TargetKind.App, on, owner, [])
                                   {
                                       IconKey = "Icon.Device.App",
                                       AppIcon = icon,
                                       Letter = name.Length > 0 ? name[..1].ToUpperInvariant() : "",
                                       Meter = meter,
                                   }, display is null ? app : $"{display} {app}");
        }
        yield return new Entry(new TargetItem("other-app", "Other app…", "", TargetKind.OtherApp, false, null, [])
                               { IconKey = "Icon.Plus" }, "browse exe");
    }

    /// <summary>Sets each running app's meter to its loudest session, and the meter of an app that
    /// stopped to 0 (the view's timer calls it).</summary>
    public void PollMeters()
    {
        if (_disposed) return;
        var audio = _sliders.Audio;
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in LiveSessions().GroupBy(s => s.ImageName, StringComparer.OrdinalIgnoreCase))
        {
            live.Add(group.Key);
            if (!_meters.TryGetValue(group.Key, out var meter)) continue;
            meter.Level = group.Max(s => (double)audio.GetSessionPeak(s.InstanceId));
        }
        foreach (var (app, meter) in _meters)
            if (!live.Contains(app)) meter.Level = 0;
    }

    /// <summary>An app started or stopped (any thread): on the UI thread the rows follow, keeping the
    /// draft, the search, the current row and the meters, and new apps' icons are asked for.</summary>
    private void OnSessionsChanged(object? sender, SessionsChangedEventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            Rebuild();
            RequestIcons();
        });

    /// <summary>Asks for the icons not asked for yet, all at once: the running apps', and the saved
    /// exe paths' of the apps on a slider or ticked here; when the batch is done the list is
    /// rebuilt once, on the UI thread.</summary>
    private void RequestIcons()
    {
        if (_icons is not { } icons) return;
        var saved = _apps.Concat(_sliders.Items.Where(i => i.IsApps).SelectMany(i => i.AppNames))
            .Select(SavedPath).OfType<string>();
        var sources = LiveSessions().Select(s => s.IconPath).Where(p => p.Length > 0).Concat(saved)
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(p => _requested.Add(p)).ToList();
        if (sources.Count == 0) return;
        _ = LoadIconsAsync(icons, sources);
    }

    private async Task LoadIconsAsync(IAppIcons icons, List<string> sources)
    {
        // Asked at 32 px for the 16 px slot, so it stays sharp at 200 % scale.
        var loaded = await Task.WhenAll(sources.Select(async source =>
        {
            Bitmap? icon = null;
            try { icon = await icons.GetIconAsync(source, 32); }
            catch (Exception ex) { AppLog.Debug($"App icon: {source} failed ({ex.GetType().Name}: {ex.Message})"); }
            return (Source: source, Icon: icon);
        }));
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            foreach (var (source, icon) in loaded) _loaded[source] = icon;
            Rebuild();
        });
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

    /// <summary>"Other app…": the picked exe's file name is ticked. Nothing awaits it, so a
    /// failure (the file picker's) is logged here.</summary>
    private async Task AddOtherAppAsync()
    {
        try
        {
            if (PickExe is not { } pick || await pick() is not { } path) return;
            var app = CommandPath.FileName(path);
            if (app.Length == 0) return;
            _paths[app.ToLowerInvariant()] = path;
            if (_type != SliderMappingType.Processes || !_apps.Exists(a => SameApp(a, app))) ToggleApp(app);
            _current = $"app:{app}";
            Changed();
            RequestIcons();
        }
        catch (Exception ex)
        {
            AppLog.Error($"Other app: could not add the app ({ex.GetType().Name}: {ex.Message})");
        }
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
        AppPaths = _type == SliderMappingType.Processes ? SlidersViewModel.PathsOf(_apps, _paths) : new Dictionary<string, string>(),
    };

    /// <summary>An app's exe path: picked here or saved on this slider, else saved on another.</summary>
    private string? SavedPath(string app)
        => _paths.TryGetValue(app.ToLowerInvariant(), out var path) ? path : _sliders.AppConfig.AppPathFor(app);

    private bool Unchanged()
    {
        if (_type != _original.Type) return false;
        return _type switch
        {
            SliderMappingType.Device => _device is null ? _original.Device is null
                                        : _original.Device is not null && Matches(_original.Device, _device),
            SliderMappingType.Processes => _apps.SequenceEqual(_original.Processes, StringComparer.OrdinalIgnoreCase)
                                           && SlidersViewModel.PathsOf(_apps, _paths).OrderBy(p => p.Key)
                                              .SequenceEqual(_original.AppPaths.OrderBy(p => p.Key)),
            _ => true,
        };
    }

    // ── Footer ────────────────────────────────────────────────────────────────

    /// <summary>Validates the draft and writes it. False, with <see cref="Error"/> set, when it is
    /// invalid, even unchanged. A valid, unchanged target closes without writing.</summary>
    public bool Done()
    {
        if (!IsStillValid()) { _close(); return false; }
        _tried = true;
        var draft = Draft();
        Error = SliderTargetValidator.Validate(draft);
        if (Error is not null) return false;
        if (!Unchanged()) _sliders.SetTarget(_slider, draft);
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

    // ── Device names ──────────────────────────────────────────────────────────

    /// <summary>Opens the device dialog (the page's); the page sets it.</summary>
    internal Action<DeviceRefConfig, AudioFlow, bool>? OpenDevice { get; init; }

    /// <summary>A device row's "Rename" (<paramref name="focusIcon"/> false) or "Icon".</summary>
    public void OpenDeviceDialog(TargetItem item, bool focusIcon)
    {
        if (item.Kind == TargetKind.Device && _deviceRows.TryGetValue(item.Key, out var device))
            OpenDevice?.Invoke(device, item.Flow, focusIcon);
    }

    /// <summary>The device dialog closed: the rows show the new names.</summary>
    void IDeviceNamesListener.DeviceNamesChanged() => Rebuild();

    public void Dispose()
    {
        _disposed = true;
        _sliders.DevicesRefreshed -= OnDevicesRefreshed;
        _sliders.Audio.SessionsChanged -= OnSessionsChanged;
    }
}
