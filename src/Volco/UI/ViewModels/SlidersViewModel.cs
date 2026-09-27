using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Volco.Audio;
using Volco.Config;

namespace Volco.UI.ViewModels;

/// <summary>
/// ViewModel for the Sliders settings tab.
/// Owns a collection of <see cref="SliderItemViewModel"/>, one per configured slider.
/// </summary>
public partial class SlidersViewModel : ObservableObject
{
    private readonly ConfigManager _config;
    private readonly IAudioEngine _audio;

    public ObservableCollection<SliderItemViewModel> Items { get; } = new();

    public SlidersViewModel(ConfigManager config, IAudioEngine audio)
    {
        _config = config;
        _audio = audio;
        BuildItems();
    }

    private void BuildItems()
    {
        Items.Clear();
        var devices = _audio.GetDevices(AudioFlow.Render);
        foreach (var sliderCfg in _config.Config.Sliders)
        {
            Items.Add(new SliderItemViewModel(sliderCfg, _audio, devices, () => _config.RequestSave()));
        }
    }

    /// <summary>
    /// Returns available output device names for the device picker in each slider row.
    /// </summary>
    public List<string> GetOutputDeviceNames() =>
        _audio.GetDevices(AudioFlow.Render).Select(d => d.FriendlyName).ToList();

    /// <summary>
    /// Returns currently active process names for auto-suggest in process-mapped sliders.
    /// GetSessions() returns live per-session entries and one process can own several, so
    /// dedupe before this reaches a UI picker.
    /// </summary>
    public List<string> GetActiveProcessNames() =>
        _audio.GetSessions()
              .Select(s => s.ImageName)
              .Where(n => !string.IsNullOrEmpty(n))
              .Distinct(StringComparer.OrdinalIgnoreCase)
              .ToList();

    /// <summary>
    /// Updates the live level bar for a specific slider.
    /// Called by Task 14 on the UI thread after marshalling from SliderProcessor.SliderLevelChanged.
    /// </summary>
    public void UpdateLiveLevel(int sliderIndex, float level)
    {
        var item = Items.FirstOrDefault(i => i.Index == sliderIndex);
        if (item != null)
            item.LiveLevel = Math.Clamp(level, 0f, 1f);
    }
}
