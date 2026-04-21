using Volco.Audio;
using Volco.Config;

namespace Volco.Input;

public class SliderProcessor : IDisposable
{
    private readonly IAudioManager _audio;
    private readonly ConfigManager _configManager;
    private readonly SessionMonitor _sessionMonitor;
    private readonly ushort?[] _pendingValues = new ushort?[16];
    private readonly Timer _coalesceTimer;
    private readonly object _lock = new();
    private HashSet<string> _mappedProcessesCache = new(StringComparer.OrdinalIgnoreCase);

    public SliderProcessor(IAudioManager audio, ConfigManager configManager, SessionMonitor sessionMonitor)
    {
        _audio = audio;
        _configManager = configManager;
        _sessionMonitor = sessionMonitor;
        _configManager.ConfigChanged += OnConfigChanged;
        UpdateMappedProcessesCache();
        _coalesceTimer = new Timer(_ => ProcessPending(), null, 20, 20);
    }

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        UpdateMappedProcessesCache();
    }

    private void UpdateMappedProcessesCache()
    {
        var config = _configManager.Config;
        var mappedProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slider in config.Sliders)
        {
            if (slider.Type == SliderMappingType.Processes)
            {
                foreach (var p in slider.Processes)
                    mappedProcesses.Add(p);
            }
        }

        lock (_lock)
        {
            _mappedProcessesCache = mappedProcesses;
        }
    }

    public static float MapAdcToVolume(ushort adcValue, bool invert)
    {
        float volume = adcValue / 1023f;
        return invert ? 1f - volume : volume;
    }

    public static List<string> GetUnmappedProcesses(AppConfig config, IReadOnlySet<string> activeProcesses)
    {
        var mappedProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slider in config.Sliders)
        {
            if (slider.Type == SliderMappingType.Processes)
            {
                foreach (var p in slider.Processes)
                    mappedProcesses.Add(p);
            }
        }

        return activeProcesses
            .Where(p => !mappedProcesses.Contains(p))
            .ToList();
    }

    public void HandleSliderEvent(int sliderIndex, ushort adcValue)
    {
        if (sliderIndex < 0 || sliderIndex >= _pendingValues.Length)
            return;

        lock (_lock)
        {
            _pendingValues[sliderIndex] = adcValue;
        }
    }

    private void ProcessPending()
    {
        var config = _configManager.Config;
        List<string>? unmappedProcessesCache = null;

        for (int sliderIndex = 0; sliderIndex < _pendingValues.Length; sliderIndex++)
        {
            ushort? value;
            lock (_lock)
            {
                value = _pendingValues[sliderIndex];
                if (value == null) continue;
                _pendingValues[sliderIndex] = null;
            }

            if (sliderIndex >= config.Sliders.Count)
                continue;

            var sliderConfig = config.Sliders[sliderIndex];
            var volume = MapAdcToVolume(value.Value, sliderConfig.Invert);

            switch (sliderConfig.Type)
            {
                case SliderMappingType.Device:
                    _audio.SetDeviceVolume(volume, sliderConfig.DeviceName);
                    break;

                case SliderMappingType.Processes:
                    _audio.SetProcessVolumes(sliderConfig.Processes, volume);
                    break;

                case SliderMappingType.Unmapped:
                    // Optimization: Cache the unmapped process list for the duration of this tick.
                    // Also use the pre-computed mapped processes cache to avoid re-scanning the config.
                    if (unmappedProcessesCache == null)
                    {
                        HashSet<string> mapped;
                        lock (_lock)
                        {
                            mapped = _mappedProcessesCache;
                        }
                        unmappedProcessesCache = _sessionMonitor.ActiveProcesses
                            .Where(p => !mapped.Contains(p))
                            .ToList();
                    }
                    _audio.SetProcessVolumes(unmappedProcessesCache, volume);
                    break;
            }
        }
    }

    public void Dispose()
    {
        _configManager.ConfigChanged -= OnConfigChanged;
        _coalesceTimer.Dispose();
    }
}
