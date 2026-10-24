using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.App.Platform;
using Volco.Core.Audio;
using Volco.Core.Config;

namespace Volco.App.ViewModels;

/// <summary>
/// The sliders of the Controller page, in hardware-input order. Position is identity, so moving a
/// slider moves its configuration to another physical slider, and only the last can be removed.
/// </summary>
public partial class SlidersViewModel : ObservableObject, ISliderCardOwner, IDisposable
{
    private readonly ConfigManager _config;
    private readonly IAudioEngine _audio;
    private readonly UndoToastViewModel _toast;
    private readonly AppIconLookup? _appIcons;
    private readonly Action<int>? _reapply;
    private IReadOnlyList<AudioDevice> _devices;

    public ObservableCollection<SliderItemViewModel> Items { get; } = new();

    /// <param name="reapply">Sets a slider's new target to the slider's last position
    /// (<c>VolcoRuntime.ReapplySlider</c>); null: nothing.</param>
    public SlidersViewModel(ConfigManager config, IAudioEngine audio, UndoToastViewModel toast, AppIconLookup? appIcons = null,
                            Action<int>? reapply = null)
    {
        _reapply = reapply;
        _config = config;
        _audio = audio;
        _toast = toast;
        _appIcons = appIcons;
        if (appIcons is not null) appIcons.Changed += RefreshAppIcons;
        _devices = AllDevices(audio, audio.GetDevices(AudioFlow.Render));
        for (int i = 0; i < config.Config.Sliders.Count; i++)
            Items.Add(NewItem(config.Config.Sliders[i], i));
        Renumber();
        AppsChanged();
    }

    private List<SliderConfig> Sliders => _config.Config.Sliders;

    internal AppConfig AppConfig => _config.Config;

    internal IAudioEngine Audio => _audio;

    /// <summary>The render devices, then the capture devices, as of the last <see cref="RefreshDevices"/>.</summary>
    internal IReadOnlyList<AudioDevice> Devices => _devices;

    private SliderItemViewModel NewItem(SliderConfig cfg, int index)
        => new(cfg, index, _devices, this, Save);

    private void Save() => _config.RequestSave();

    /// <summary>A "Not set up" slider for a control the Controller page's draft adds: not in
    /// <see cref="Items"/> and never saved (Volco's settings get it once the controller confirms).</summary>
    public SliderItemViewModel CreateDetached(int index)
        => new(new SliderConfig { Index = index, Type = SliderMappingType.None }, index, _devices, this, () => { }) { IsNew = true };

    /// <summary>Rebuilds <see cref="Items"/> from <c>config.json</c> after its list was rewritten
    /// (a confirmed save removed or added controls). Live levels stay with their positions.</summary>
    public void Reload()
    {
        var levels = Items.Select(i => i.LiveLevel).ToList();
        Items.Clear();
        for (int i = 0; i < Sliders.Count; i++)
        {
            var item = NewItem(Sliders[i], i);
            if (i < levels.Count) item.LiveLevel = levels[i];
            Items.Add(item);
        }
        Renumber();
        AppsChanged();
    }

    // ── Add / remove ──────────────────────────────────────────────────────────

    /// <summary>Adds a "Not set up" slider for each input the controller has beyond the list.</summary>
    [RelayCommand]
    public void AddMissing()
    {
        var target = _config.Config.Controller?.Sliders ?? 0;
        if (Items.Count >= target) return;
        while (Items.Count < target)
        {
            var cfg = new SliderConfig { Type = SliderMappingType.None };
            Sliders.Add(cfg);
            Items.Add(NewItem(cfg, Items.Count));
        }
        Renumber();
        AppsChanged();
        Save();
    }

    public void RemoveLast()
    {
        if (Items.Count == 0) return;
        var item = Items[^1];
        var cfg = Sliders[^1];
        Items.RemoveAt(Items.Count - 1);
        Sliders.RemoveAt(Sliders.Count - 1);
        Renumber();
        AppsChanged();
        Save();
        _toast.Show($"{item.Heading} removed", () =>
        {
            Sliders.Add(cfg);
            Items.Add(item);
            Renumber();
            AppsChanged();
            Save();
        });
    }

    // ── Reordering ────────────────────────────────────────────────────────────

    /// <summary>Swaps what two sliders control and their names. The response settings and the
    /// live level stay with each physical slider.</summary>
    public void Swap(int a, int b)
    {
        if (a == b || a < 0 || b < 0 || a >= Items.Count || b >= Items.Count) return;
        DoSwap(a, b);
        Save();
        _toast.Show($"Swapped slider {a + 1} and slider {b + 1}", () =>
        {
            DoSwap(a, b);
            Save();
        });
    }

    private void DoSwap(int a, int b)
    {
        var x = Sliders[a];
        var y = Sliders[b];
        (x.Name, y.Name) = (y.Name, x.Name);
        (x.Type, y.Type) = (y.Type, x.Type);
        (x.Device, y.Device) = (y.Device, x.Device);
        (x.Processes, y.Processes) = (y.Processes, x.Processes);
        (x.AppPaths, y.AppPaths) = (y.AppPaths, x.AppPaths);
        Items[a].Reload();
        Items[b].Reload();
        AppsChanged();
        // Each physical slider still sits where it was: its new target follows it.
        _reapply?.Invoke(a);
        _reapply?.Invoke(b);
    }

    private void Renumber()
    {
        for (int i = 0; i < Items.Count; i++)
        {
            Items[i].Index = i;
            Items[i].IsLast = i == Items.Count - 1;
        }
    }

    public DeviceChoice Describe(string friendlyName) => DeviceChoice.For(_config.Config, _devices, friendlyName);

    /// <summary>Refreshes every "All other apps" hint and app icon after any slider's apps or type changed.</summary>
    public void AppsChanged()
    {
        RefreshAppIcons();
        foreach (var item in Items)
        {
            if (!item.IsOtherApps) continue;
            var claimed = Items.Where(o => o != item && o.IsApps)
                .SelectMany(o => o.AppNames)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            item.SetOtherAppsHint(claimed);
        }
    }

    /// <summary>Each apps slider shows the icon of its first app that has one.</summary>
    private void RefreshAppIcons()
    {
        foreach (var item in Items)
            item.AppIcon = item.IsApps ? _appIcons?.Get(item.AppNames) : null;
    }

    public void Dispose()
    {
        if (_appIcons is not null) _appIcons.Changed -= RefreshAppIcons;
    }

    // ── Live updates (UI thread) ──────────────────────────────────────────────

    /// <summary>Updates the level bar of the slider at hardware input <paramref name="sliderIndex"/>
    /// and flashes its tile.</summary>
    public void UpdateLiveLevel(int sliderIndex, float level)
    {
        if (sliderIndex < 0 || sliderIndex >= Items.Count) return;
        var item = Items[sliderIndex];
        item.LiveLevel = Math.Clamp(level, 0f, 1f);
        item.Pulse();
    }

    /// <summary>
    /// Seeds every slider's level from <paramref name="getLevel"/> (the runtime's last known levels):
    /// the controller only reports a slider when it moves, so a window opened later would otherwise
    /// show nothing until each slider moved again. Unknown levels are left alone.
    /// </summary>
    public void SeedLiveLevels(Func<int, float?> getLevel)
    {
        foreach (var item in Items)
            if (getLevel(item.Index) is { } level)
                item.LiveLevel = Math.Clamp(level, 0f, 1f);
    }

    /// <summary>Re-reads every slider's devices. Callers are on the UI thread.</summary>
    public void RefreshDevices(IReadOnlyList<AudioDevice> renderDevices)
    {
        var devices = AllDevices(_audio, renderDevices);
        _devices = devices;
        foreach (var item in Items)
            item.RefreshDevices(devices);
        DevicesRefreshed?.Invoke(devices);
    }

    private static IReadOnlyList<AudioDevice> AllDevices(IAudioEngine audio, IReadOnlyList<AudioDevice> render)
        => render.Where(d => d.Flow == AudioFlow.Render).Concat(audio.GetDevices(AudioFlow.Capture)).ToList();

    /// <summary>After <see cref="RefreshDevices"/>: an open target dialog's device rows follow.</summary>
    internal event Action<IReadOnlyList<AudioDevice>>? DevicesRefreshed;

    // ── Target dialog ─────────────────────────────────────────────────────────

    /// <summary>
    /// The target dialog's Done (spec controls-page §6.2): <paramref name="item"/> takes the type,
    /// device and apps of <paramref name="target"/>. Its apps leave every other slider, and another
    /// slider set to everything else becomes an apps slider with no apps.
    /// </summary>
    internal void SetTarget(SliderItemViewModel item, SliderConfig target)
    {
        foreach (var other in Items)
        {
            if (other == item) continue;
            var cfg = other.Config;
            if (target.Type == SliderMappingType.Processes && cfg.Type == SliderMappingType.Processes)
            {
                var kept = cfg.Processes.Where(p => !target.Processes.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
                if (kept.Count == cfg.Processes.Count) continue;
                cfg.Processes = kept;
                cfg.AppPaths = PathsOf(kept, cfg.AppPaths);
            }
            else if (target.Type == SliderMappingType.Unmapped && cfg.Type == SliderMappingType.Unmapped)
            {
                cfg.Type = SliderMappingType.Processes;
                cfg.Processes = new List<string>();
                cfg.AppPaths = new Dictionary<string, string>();
            }
            else continue;
            other.Reload();
        }
        item.Config.Type = target.Type;
        item.Config.Device = target.Type == SliderMappingType.Device ? target.Device : null;
        item.Config.Processes = target.Type == SliderMappingType.Processes ? new List<string>(target.Processes) : new List<string>();
        // The dialog's paths (its picks) over the ones the slider already had, for the apps it keeps.
        var paths = new Dictionary<string, string>(item.Config.AppPaths);
        foreach (var (app, path) in target.AppPaths) paths[app] = path;
        item.Config.AppPaths = PathsOf(item.Config.Processes, paths);
        item.Reload();
        AppsChanged();
        Save();
        _reapply?.Invoke(item.Index);
    }

    /// <summary>The entries of <paramref name="paths"/> for <paramref name="apps"/> only: a path goes
    /// when its app leaves the slider.</summary>
    internal static Dictionary<string, string> PathsOf(IEnumerable<string> apps, IReadOnlyDictionary<string, string> paths)
    {
        var result = new Dictionary<string, string>();
        foreach (var app in apps)
            if (paths.TryGetValue(app.ToLowerInvariant(), out var path) && path.Length > 0)
                result[app.ToLowerInvariant()] = path;
        return result;
    }

    // ── Config copies ─────────────────────────────────────────────────────────

    internal static SliderConfig Clone(SliderConfig s) => new()
    {
        Index = s.Index,
        Name = s.Name,
        Type = s.Type,
        Processes = new List<string>(s.Processes),
        AppPaths = new Dictionary<string, string>(s.AppPaths),
        Device = s.Device is null ? null : new DeviceRefConfig { Id = s.Device.Id, FriendlyName = s.Device.FriendlyName },
    };
}
