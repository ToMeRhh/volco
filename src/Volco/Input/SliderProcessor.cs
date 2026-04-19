using Volco.Audio;
using Volco.Config;

namespace Volco.Input;

public class SliderProcessor
{
    private readonly IAudioManager _audio;
    private readonly ConfigManager _configManager;
    private readonly SessionMonitor _sessionMonitor;

    public SliderProcessor(IAudioManager audio, ConfigManager configManager, SessionMonitor sessionMonitor)
    {
        _audio = audio;
        _configManager = configManager;
        _sessionMonitor = sessionMonitor;
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
        var config = _configManager.Config;
        if (sliderIndex < 0 || sliderIndex >= config.Sliders.Count)
            return;

        var sliderConfig = config.Sliders[sliderIndex];
        var volume = MapAdcToVolume(adcValue, sliderConfig.Invert);

        switch (sliderConfig.Type)
        {
            case SliderMappingType.Device:
                _audio.SetDeviceVolume(volume);
                break;

            case SliderMappingType.Processes:
                foreach (var processName in sliderConfig.Processes)
                    _audio.SetProcessVolume(processName, volume);
                break;

            case SliderMappingType.Unmapped:
                var unmapped = GetUnmappedProcesses(config, _sessionMonitor.ActiveProcesses);
                foreach (var processName in unmapped)
                    _audio.SetProcessVolume(processName, volume);
                break;
        }
    }
}
