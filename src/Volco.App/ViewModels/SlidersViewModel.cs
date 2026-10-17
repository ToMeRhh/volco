using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Audio;
using Volco.Core.Config;

namespace Volco.App.ViewModels;

/// <summary>
/// The Sliders page: one card per configured slider, in hardware-input order. Position is
/// identity, so reordering moves configuration between physical sliders, a middle card can only be
/// reset (never removed), and only the last card can be removed.
/// </summary>
public partial class SlidersViewModel : ObservableObject, IReorderableList, ISliderCardOwner
{
    private readonly ConfigManager _config;
    private readonly IAudioEngine _audio;
    private readonly UndoToastViewModel _toast;
    private IReadOnlyList<AudioDevice> _devices;

    public ObservableCollection<SliderItemViewModel> Items { get; } = new();

    public SlidersViewModel(ConfigManager config, IAudioEngine audio, UndoToastViewModel toast)
    {
        _config = config;
        _audio = audio;
        _toast = toast;
        _devices = audio.GetDevices(AudioFlow.Render);
        for (int i = 0; i < config.Config.Sliders.Count; i++)
            Items.Add(NewItem(config.Config.Sliders[i], i));
        Renumber();
        AppsChanged();
        RefreshController();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCountHint))]
    private string _countHint = "";

    public bool HasCountHint => CountHint.Length > 0;

    [ObservableProperty]
    private bool _canAddMissing;

    [ObservableProperty]
    private string _addMissingText = "";

    /// <summary>Re-reads the controller's hardware (a Hello arrived) or the card count changed.</summary>
    public void RefreshController()
    {
        var n = _config.Config.Controller?.Sliders;
        CountHint = CountHints.Text("slider", n, Items.Count);
        var missing = (n ?? 0) - Items.Count;
        CanAddMissing = missing > 0;
        AddMissingText = missing == 1 ? "Add the missing one" : $"Add the missing {missing}";
    }

    private List<SliderConfig> Sliders => _config.Config.Sliders;

    private SliderItemViewModel NewItem(SliderConfig cfg, int index)
        => new(cfg, index, _audio, _devices, this, Save);

    private void Save() => _config.RequestSave();

    // ── Add / reset / remove ──────────────────────────────────────────────────

    /// <summary>Adds a "Not set up" slider for the next hardware input, opened for editing.</summary>
    [RelayCommand]
    private void Add()
    {
        var cfg = new SliderConfig { Type = SliderMappingType.None };
        Sliders.Add(cfg);
        var item = NewItem(cfg, Items.Count);
        item.IsExpanded = true;
        Items.Add(item);
        Renumber();
        AppsChanged();
        RefreshController();
        Save();
    }

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
        RefreshController();
        Save();
    }

    public void Reset(SliderItemViewModel item)
    {
        var before = Clone(item.Config);
        CopyInto(item.Config, new SliderConfig { Type = SliderMappingType.None });
        item.Reload();
        item.IsAdvancedOpen = false;
        AppsChanged();
        Save();
        _toast.Show($"{item.Title} reset", () =>
        {
            CopyInto(item.Config, before);
            item.Reload();
            AppsChanged();
            Save();
        });
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
        RefreshController();
        Save();
        _toast.Show($"{item.Title} removed", () =>
        {
            Sliders.Add(cfg);
            Items.Add(item);
            Renumber();
            AppsChanged();
            RefreshController();
            Save();
        });
    }

    // ── Reordering ────────────────────────────────────────────────────────────

    public int Count => Items.Count;

    public void MoveUp(SliderItemViewModel item)
    {
        var i = Items.IndexOf(item);
        if (i > 0) Move(i, i - 1);
    }

    public void MoveDown(SliderItemViewModel item)
    {
        var i = Items.IndexOf(item);
        if (i >= 0 && i < Items.Count - 1) Move(i, i + 1);
    }

    public void Move(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= Items.Count || to >= Items.Count) { Renumber(); return; }
        var cfg = Sliders[from];
        Sliders.RemoveAt(from);
        Sliders.Insert(to, cfg);
        Items.Move(from, to);
        Renumber();
        Save();
    }

    public void PreviewMove(int from, int to)
    {
        for (int i = 0; i < Items.Count; i++)
            Items[i].Index = PreviewIndex(i, from, to);
    }

    public void CancelPreview() => Renumber();

    /// <summary>Where the card at <paramref name="i"/> would land if <paramref name="from"/> moved to <paramref name="to"/>.</summary>
    internal static int PreviewIndex(int i, int from, int to)
    {
        if (i == from) return to;
        if (from < to && i > from && i <= to) return i - 1;
        if (to < from && i >= to && i < from) return i + 1;
        return i;
    }

    private void Renumber()
    {
        for (int i = 0; i < Items.Count; i++)
        {
            Items[i].Index = i;
            Items[i].IsLast = i == Items.Count - 1;
        }
    }

    /// <summary>Refreshes every "All other apps" hint after any slider's apps or type changed.</summary>
    public DeviceChoice Describe(string friendlyName) => DeviceChoice.For(_config.Config, _devices, friendlyName);

    public void AppsChanged()
    {
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

    // ── Live updates (UI thread) ──────────────────────────────────────────────

    /// <summary>Updates the level bar of the slider at hardware input <paramref name="sliderIndex"/>
    /// and flashes its card.</summary>
    public void UpdateLiveLevel(int sliderIndex, float level)
    {
        if (sliderIndex < 0 || sliderIndex >= Items.Count) return;
        var item = Items[sliderIndex];
        item.LiveLevel = Math.Clamp(level, 0f, 1f);
        item.Pulse();
    }

    /// <summary>
    /// Seeds every card's level from <paramref name="getLevel"/> (the runtime's last known levels):
    /// the controller only reports a slider when it moves, so a window opened later would otherwise
    /// show nothing until each slider moved again. Unknown levels are left alone.
    /// </summary>
    public void SeedLiveLevels(Func<int, float?> getLevel)
    {
        foreach (var item in Items)
            if (getLevel(item.Index) is { } level)
                item.LiveLevel = Math.Clamp(level, 0f, 1f);
    }

    /// <summary>Re-seeds every card's device picker. Callers are on the UI thread.</summary>
    public void RefreshDevices(IReadOnlyList<AudioDevice> devices)
    {
        _devices = devices;
        foreach (var item in Items)
            item.RefreshDevices(devices);
    }

    // ── Config copies (for Undo) ──────────────────────────────────────────────

    internal static SliderConfig Clone(SliderConfig s) => new()
    {
        Index = s.Index,
        Name = s.Name,
        Type = s.Type,
        Processes = new List<string>(s.Processes),
        Device = s.Device is null ? null : new DeviceRefConfig { Id = s.Device.Id, FriendlyName = s.Device.FriendlyName },
        Invert = s.Invert,
        Curve = s.Curve,
        DeadZonePercent = s.DeadZonePercent,
        SmoothingFactor = s.SmoothingFactor,
    };

    private static void CopyInto(SliderConfig target, SliderConfig source)
    {
        var copy = Clone(source);
        target.Name = copy.Name;
        target.Type = copy.Type;
        target.Processes = copy.Processes;
        target.Device = copy.Device;
        target.Invert = copy.Invert;
        target.Curve = copy.Curve;
        target.DeadZonePercent = copy.DeadZonePercent;
        target.SmoothingFactor = copy.SmoothingFactor;
    }
}
