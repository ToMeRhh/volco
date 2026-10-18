using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Devices;

namespace Volco.App.ViewModels;

/// <summary>
/// The Devices page (spec §6.1): every enabled device, plus disconnected ones that have settings
/// or are used by a slider or button action, in Outputs and Inputs; default device first, then by
/// display name. Nicknames and icons save as they are edited.
/// </summary>
public sealed partial class DevicesViewModel : ObservableObject
{
    private readonly ConfigManager _config;
    private readonly IAudioEngine _audio;

    public ObservableCollection<DeviceItemViewModel> Outputs { get; } = new();
    public ObservableCollection<DeviceItemViewModel> Inputs { get; } = new();

    public DevicesViewModel(ConfigManager config, IAudioEngine audio)
    {
        _config = config;
        _audio = audio;
        Refresh();
    }

    internal AppConfig Config => _config.Config;

    /// <summary>A nickname or icon was saved, or a device forgotten: other pages showing display
    /// names (pickers, card summaries, screen previews) should re-read them.</summary>
    public event EventHandler? DeviceSettingsChanged;

    internal void Save()
    {
        _config.RequestSave();
        DeviceSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Rebuilds both lists (device plugged in or out, default changed); keeps open cards
    /// open. When the same devices are listed with the same status, the cards are kept (so a
    /// nickname being typed keeps its text box) and only their "Used by" lines are re-read.</summary>
    public void Refresh()
    {
        var open = Outputs.Concat(Inputs).Where(i => i.IsExpanded).Select(i => i.Id)
                          .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Fill(Outputs, AudioFlow.Render, open);
        Fill(Inputs, AudioFlow.Capture, open);
    }

    private void Fill(ObservableCollection<DeviceItemViewModel> list, AudioFlow flow, HashSet<string> open)
    {
        var live = _audio.GetDevices(flow);
        var defaultId = _audio.GetDefaultDevice(flow)?.Id;
        var items = new List<DeviceItemViewModel>();
        bool Known(string id) => items.Exists(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));

        foreach (var d in live)
            items.Add(new DeviceItemViewModel(this, d.Id, flow, d.FriendlyName, DeviceDisplay.IconFor(d),
                isConnected: true, isDefault: string.Equals(d.Id, defaultId, StringComparison.OrdinalIgnoreCase)));
        foreach (var s in Config.Devices.Where(s => s.Flow == flow && !Known(s.Id)))
            items.Add(new DeviceItemViewModel(this, s.Id, flow, s.FriendlyName, DeviceDisplay.IconFor(Volco.Core.Audio.DeviceFormFactor.Unknown, flow), false, false));
        foreach (var r in References(flow).Where(r => !string.IsNullOrEmpty(r.Id) && !Known(r.Id)))
            items.Add(new DeviceItemViewModel(this, r.Id, flow, r.FriendlyName, DeviceDisplay.IconFor(Volco.Core.Audio.DeviceFormFactor.Unknown, flow), false, false));

        static (string, bool, bool, string) Key(DeviceItemViewModel i)
            => (i.Id.ToUpperInvariant(), i.IsConnected, i.IsDefault, i.WindowsName);
        if (list.Count == items.Count && list.Select(Key).ToHashSet().SetEquals(items.Select(Key)))
        {
            foreach (var item in list) item.RefreshUsage();
            return;
        }

        list.Clear();
        foreach (var item in items.OrderByDescending(i => i.IsDefault)
                                  .ThenBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            item.IsExpanded = open.Contains(item.Id);
            list.Add(item);
        }
    }


    /// <summary>Device references in sliders and button actions (all are outputs today).</summary>
    private IEnumerable<DeviceRefConfig> References(AudioFlow flow)
    {
        if (flow != AudioFlow.Render) yield break;
        foreach (var s in Config.Sliders)
            if (s.Device is { } d) yield return d;
        foreach (var a in Config.Buttons.SelectMany(b => b.Actions.Values).Where(b => b is not null).SelectMany(b => b.All()))
        {
            if (a.Device is { } d1) yield return d1;
            if (a.DeviceB is { } d2) yield return d2;
        }
    }

    /// <summary>"Used by Slider 0, Button 2" (numbers as on the cards), or "".</summary>
    internal string UsedBy(string id)
    {
        bool Is(DeviceRefConfig? r) => r is not null && string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase);
        var users = new List<string>();
        for (int i = 0; i < Config.Sliders.Count; i++)
            if (Is(Config.Sliders[i].Device)) users.Add($"Slider {i}");
        for (int i = 0; i < Config.Buttons.Count; i++)
            if (Config.Buttons[i].Actions.Values.Where(b => b is not null).SelectMany(b => b.All()).Any(a => Is(a.Device) || Is(a.DeviceB)))
                users.Add($"Button {i}");
        return users.Count == 0 ? "" : "Used by " + string.Join(", ", users);
    }

    internal void Forget(DeviceItemViewModel item)
    {
        Config.Devices.RemoveAll(d => string.Equals(d.Id, item.Id, StringComparison.OrdinalIgnoreCase));
        Save();
        Refresh();
    }
}
