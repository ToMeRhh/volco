using System.Diagnostics;
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Diagnostics;

namespace Volco.Core.Input;

/// <summary>
/// Event args for <see cref="SliderProcessor.SliderLevelChanged"/>.
/// Carries the slider index and the final (post-curve, post-smoothing) volume in [0,1].
/// </summary>
public sealed class SliderLevelEventArgs : EventArgs
{
    public int SliderIndex { get; }
    public float Level { get; }

    public SliderLevelEventArgs(int sliderIndex, float level)
    {
        SliderIndex = sliderIndex;
        Level = level;
    }
}

public class SliderProcessor : IDisposable
{
    private readonly IAudioEngine _audio;
    private readonly ConfigManager _configManager;
    private readonly ushort?[] _pendingValues = new ushort?[16];
    private readonly float?[] _lastVolume = new float?[16];
    private readonly Timer _coalesceTimer;
    private readonly object _lock = new();

    /// <summary>Raised after a slider changes volume, so the UI can show an OSD flyout.</summary>
    public event EventHandler<VolumeChangeNotification>? VolumeChanged;

    /// <summary>
    /// Raised after each slider's final (post-curve, post-smoothing) volume is computed.
    /// Carries the slider index and level in [0,1]. Additive — does not replace VolumeChanged.
    /// Task 14 wires this to SlidersViewModel.UpdateLiveLevel on the UI thread.
    /// </summary>
    public event EventHandler<SliderLevelEventArgs>? SliderLevelChanged;

    public SliderProcessor(IAudioEngine audio, ConfigManager configManager)
    {
        _audio = audio;
        _configManager = configManager;
        _audio.SessionsChanged += OnSessionsChanged;
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
        var mapped = new HashSet<string>(MappedProcessNames(config), StringComparer.OrdinalIgnoreCase);
        return activeProcesses.Where(p => !mapped.Contains(p)).ToList();
    }

    /// <summary>Resolves a slider's configured device to a <see cref="DeviceRef"/>.</summary>
    internal static DeviceRef DeviceRefFor(SliderConfig slider)
    {
        if (slider.Device == null) return DeviceRef.DefaultOutput();
        if (!string.IsNullOrEmpty(slider.Device.Id)) return DeviceRef.ById(slider.Device.Id);
        if (!string.IsNullOrEmpty(slider.Device.FriendlyName)) return DeviceRef.ByFriendlyName(slider.Device.FriendlyName);
        return DeviceRef.DefaultOutput();
    }

    /// <summary>Every process name claimed by a Processes-type slider.</summary>
    internal static List<string> MappedProcessNames(AppConfig config)
    {
        var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slider in config.Sliders)
            if (slider.Type == SliderMappingType.Processes)
                foreach (var p in slider.Processes)
                    mapped.Add(p);
        return mapped.ToList();
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

            float? prevVolume;
            lock (_lock)
            {
                prevVolume = _lastVolume[sliderIndex];
            }

            if (sliderConfig.SmoothingFactor > 0 && prevVolume is { } prev)
                volume = ApplySmoothing(prev, volume, sliderConfig.SmoothingFactor);

            lock (_lock)
            {
                _lastVolume[sliderIndex] = volume;
            }

            // Only allocated when actually needed (review item 10): this ~50Hz-per-moving-slider
            // path exists to be cheap, and a Stopwatch that exists purely to feed a debug-only
            // log line must not be built when debug logging is off.
            Stopwatch? sw = AppLog.IsDebugEnabled ? Stopwatch.StartNew() : null;

            switch (sliderConfig.Type)
            {
                case SliderMappingType.Device:
                    AppLog.Debug($"[Slider] index={sliderIndex} type=Device device={sliderConfig.DeviceName} adc={value} vol={volume:F3}");
                    _audio.SetDeviceVolume(DeviceRefFor(sliderConfig), volume);
                    break;

                case SliderMappingType.Processes:
                    AppLog.Debug($"[Slider] index={sliderIndex} type=Processes procs=[{string.Join(",", sliderConfig.Processes)}] adc={value} vol={volume:F3}");
                    _audio.SetSessionVolume(new SessionFilter.ByImageNames(sliderConfig.Processes), volume);
                    break;

                case SliderMappingType.Unmapped:
                    var mappedNames = MappedProcessNames(config);
                    AppLog.Debug($"[Slider] index={sliderIndex} type=Unmapped mappedCount={mappedNames.Count} adc={value} vol={volume:F3}");
                    _audio.SetSessionVolume(new SessionFilter.Unmapped(mappedNames), volume);
                    break;
            }

            if (sw is not null)
                AppLog.Debug($"[Slider] index={sliderIndex} dispatch took {sw.ElapsedMilliseconds}ms");

            SliderLevelChanged?.Invoke(this, new SliderLevelEventArgs(sliderIndex, volume));

            if (VolumeChanged != null)
            {
                var currentOutput = sliderConfig.Type == SliderMappingType.Device
                                    && string.IsNullOrEmpty(sliderConfig.DeviceName)
                    ? _audio.GetDefaultDevice(AudioFlow.Render)?.FriendlyName
                    : null;
                var label = BuildSliderLabel(sliderConfig, currentOutput);
                VolumeChanged.Invoke(this, new VolumeChangeNotification(label, volume, Muted: false, OsdKind.Volume));
            }
        }
    }

    /// <summary>
    /// A newly created session that a Processes slider claims gets that slider's current
    /// level applied immediately, so an app launched mid-session does not play at whatever
    /// volume Windows gave it.
    /// </summary>
    private void OnSessionsChanged(object? sender, SessionsChangedEventArgs e)
    {
        if (e.Kind != SessionChangeKind.Created) return;

        var config = _configManager.Config;

        // Pass 1: does any Processes slider CLAIM this image name? A claim is decided purely
        // by slider.Processes membership, independent of whether that slider has moved yet —
        // otherwise an unmoved-but-mapped slider would let the Unmapped slider (pass 2) steal
        // its process, which is exactly the bug this two-pass split prevents.
        for (int i = 0; i < config.Sliders.Count && i < _lastVolume.Length; i++)
        {
            var slider = config.Sliders[i];
            if (slider.Type != SliderMappingType.Processes) continue;
            if (!slider.Processes.Any(p => string.Equals(p, e.Session.ImageName, StringComparison.OrdinalIgnoreCase)))
                continue;

            float? level;
            lock (_lock)
            {
                level = _lastVolume[i];
            }
            if (level is { } lvl)
                _audio.SetSessionVolume(new SessionFilter.ByImageNames(new[] { e.Session.ImageName }), lvl);
            return; // claimed either way — must NOT fall through to the Unmapped pass.
        }

        // Pass 2: genuinely unclaimed — an Unmapped slider owns it, if one exists and has moved.
        for (int i = 0; i < config.Sliders.Count && i < _lastVolume.Length; i++)
        {
            if (config.Sliders[i].Type != SliderMappingType.Unmapped) continue;

            float? level;
            lock (_lock)
            {
                level = _lastVolume[i];
            }
            if (level is { } lvl)
            {
                _audio.SetSessionVolume(new SessionFilter.ByImageNames(new[] { e.Session.ImageName }), lvl);
                return;
            }
        }
    }

    public void Dispose()
    {
        _audio.SessionsChanged -= OnSessionsChanged;
        _coalesceTimer.Dispose();
    }
}
