using System.Diagnostics;
using Volco.Audio;
using Volco.Config;

namespace Volco.Input;

public class SliderProcessor : IDisposable
{
    private readonly IAudioManager _audio;
    private readonly ConfigManager _configManager;
    private readonly SessionMonitor _sessionMonitor;
    private readonly ushort?[] _pendingValues = new ushort?[16];
    private readonly float?[] _lastVolume = new float?[16];
    private readonly Timer _coalesceTimer;
    private readonly object _lock = new();

    /// <summary>Raised after a slider changes volume, so the UI can show an OSD flyout.</summary>
    public event EventHandler<VolumeChangeNotification>? VolumeChanged;

    public SliderProcessor(IAudioManager audio, ConfigManager configManager, SessionMonitor sessionMonitor)
    {
        _audio = audio;
        _configManager = configManager;
        _sessionMonitor = sessionMonitor;
        _coalesceTimer = new Timer(_ => ProcessPending(), null, 20, 20);
    }

    public static float MapAdcToVolume(ushort adcValue, bool invert)
        => MapAdcToVolume(adcValue, invert, ResponseCurve.Linear, 0);

    public static float MapAdcToVolume(ushort adcValue, bool invert, ResponseCurve curve, double deadZonePercent)
    {
        float raw = adcValue / 4095f;
        if (invert) raw = 1f - raw;

        var dz = (float)Math.Clamp(deadZonePercent, 0, 0.45);
        if (dz > 0)
            raw = raw <= dz ? 0f : (raw - dz) / (1f - dz);

        return ApplyCurve(raw, curve);
    }

    public static float ApplySmoothing(float previous, float target, double smoothingFactor)
    {
        var a = (float)Math.Clamp(smoothingFactor, 0, 0.95);
        return previous + (target - previous) * (1f - a);
    }

    private static float ApplyCurve(float x, ResponseCurve curve) => curve switch
    {
        ResponseCurve.Linear      => x,
        ResponseCurve.Logarithmic => x <= 0 ? 0 : MathF.Pow(x, 0.5f),
        ResponseCurve.Exponential => MathF.Pow(x, 2f),
        ResponseCurve.SCurve      => x * x * (3f - 2f * x), // smoothstep
        _                         => x
    };

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

    /// <summary>
    /// Builds the human-readable label shown on the OSD for a slider.
    /// <paramref name="currentOutputName"/> is only used for Device sliders with
    /// an empty DeviceName (i.e. the "current output device").
    /// </summary>
    public static string BuildSliderLabel(SliderConfig slider, string? currentOutputName)
    {
        switch (slider.Type)
        {
            case SliderMappingType.Device:
                if (!string.IsNullOrEmpty(slider.DeviceName))
                    return slider.DeviceName;
                return string.IsNullOrEmpty(currentOutputName) ? "Output" : currentOutputName;

            case SliderMappingType.Processes:
                if (slider.Processes.Count == 0)
                    return "Apps";
                return string.Join(", ", slider.Processes.Select(FriendlyProcessName));

            case SliderMappingType.Unmapped:
                return "Other apps";

            default:
                return "Volume";
        }
    }

    private static string FriendlyProcessName(string processName)
    {
        var name = processName;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        if (name.Length == 0)
            return name;
        return char.ToUpperInvariant(name[0]) + name[1..];
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
            var volume = MapAdcToVolume(value.Value, sliderConfig.Invert, sliderConfig.Curve, sliderConfig.DeadZonePercent);

            if (sliderConfig.SmoothingFactor > 0 && _lastVolume[sliderIndex] is { } prev)
                volume = ApplySmoothing(prev, volume, sliderConfig.SmoothingFactor);
            _lastVolume[sliderIndex] = volume;

            var sw = Stopwatch.StartNew();

            switch (sliderConfig.Type)
            {
                case SliderMappingType.Device:
                    AppLog.Debug($"[Slider] index={sliderIndex} type=Device device={sliderConfig.DeviceName} adc={value} vol={volume:F3}");
                    _audio.SetDeviceVolume(volume, sliderConfig.DeviceName);
                    break;

                case SliderMappingType.Processes:
                    AppLog.Debug($"[Slider] index={sliderIndex} type=Processes procs=[{string.Join(",", sliderConfig.Processes)}] adc={value} vol={volume:F3}");
                    _audio.SetProcessVolumes(sliderConfig.Processes, volume);
                    break;

                case SliderMappingType.Unmapped:
                    var unmapped = GetUnmappedProcesses(config, _sessionMonitor.ActiveProcesses);
                    AppLog.Debug($"[Slider] index={sliderIndex} type=Unmapped unmappedCount={unmapped.Count} adc={value} vol={volume:F3}");
                    _audio.SetProcessVolumes(unmapped, volume);
                    break;
            }

            AppLog.Debug($"[Slider] index={sliderIndex} dispatch took {sw.ElapsedMilliseconds}ms");

            if (VolumeChanged != null)
            {
                var currentOutput = sliderConfig.Type == SliderMappingType.Device
                                    && string.IsNullOrEmpty(sliderConfig.DeviceName)
                    ? _audio.GetDefaultOutputDeviceName()
                    : null;
                var label = BuildSliderLabel(sliderConfig, currentOutput);
                VolumeChanged.Invoke(this, new VolumeChangeNotification(label, volume, Muted: false, OsdKind.Volume));
            }
        }
    }

    public void Dispose()
    {
        _coalesceTimer.Dispose();
    }
}
