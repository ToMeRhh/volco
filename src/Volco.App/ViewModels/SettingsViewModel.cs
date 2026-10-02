using CommunityToolkit.Mvvm.ComponentModel;
using Volco.Core.Input;
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Runtime;

namespace Volco.App.ViewModels;

/// <summary>
/// Root ViewModel for the Settings window. Lazily creates one ViewModel per page, so state and
/// subscriptions persist across page switches, and owns every runtime/engine subscription the
/// pages need, marshalling each event to the UI thread before touching bound state.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private DevicesViewModel? _devicesViewModel;
    private readonly ConfigManager _config;
    private readonly IAudioEngine _audio;
    private readonly VolcoRuntime _runtime;

    // Runtime and engine events fire on arbitrary threads (audio thread, SliderProcessor's
    // coalescing timer, transport threads). Every handler posts through this first.
    private readonly IUiDispatcher _ui;
    private bool _disposed;

    private ConnectionViewModel? _connectionViewModel;
    private SlidersViewModel? _slidersViewModel;
    private ButtonsViewModel? _buttonsViewModel;
    private GeneralViewModel? _generalViewModel;

    /// <summary>The window's one "… · Undo" message.</summary>
    public UndoToastViewModel Toast { get; } = new();

    public ConnectionViewModel Connection =>
        _connectionViewModel ??= new ConnectionViewModel(_config, _runtime, _ui);

    public SlidersViewModel Sliders =>
        _slidersViewModel ??= new SlidersViewModel(_config, _audio, Toast);

    public DevicesViewModel Devices => _devicesViewModel ??= new DevicesViewModel(_config, _audio);

    public ButtonsViewModel Buttons =>
        _buttonsViewModel ??= new ButtonsViewModel(_config, _audio, _runtime, _runtime.Execute, Toast);

    public GeneralViewModel General =>
        _generalViewModel ??= new GeneralViewModel(_config);

    public SettingsViewModel(ConfigManager config, IAudioEngine audio, VolcoRuntime runtime, IUiDispatcher ui)
    {
        _config = config;
        _audio = audio;
        _runtime = runtime;
        _ui = ui;

        _runtime.DevicesChanged += OnDevicesChanged;
        _runtime.SliderLevelChanged += OnSliderLevelChanged;
        _runtime.ButtonActivity += OnButtonActivity;
        _runtime.ActivePresetChanged += OnActivePresetChanged;
        _audio.DeviceVolumeChanged += OnDeviceVolumeChanged;

        // The controller only reports a slider when it moves, so levels reported before this VM
        // existed would never reach the bars. Seed them now; the subscription above (kept for the
        // VM's whole lifetime, even while the window is hidden) keeps them current.
        Sliders.SeedLiveLevels(_runtime.GetSliderLevel);
    }

    // ── Runtime / engine event handlers (any thread) ──────────────────────────

    /// <summary>Every kind of device change (added/removed/state/default) refreshes every open
    /// device picker and the light previews. Unopened pages build fresh lists when opened.</summary>
    private void OnDevicesChanged(object? sender, DevicesChangedEventArgs e)
        => _ui.Post(() =>
        {
            if (_disposed) return;
            var devices = _audio.GetDevices(AudioFlow.Render);
            _slidersViewModel?.RefreshDevices(devices);
            _buttonsViewModel?.RefreshDevices(devices);
            _devicesViewModel?.Refresh();
        });

    private void OnSliderLevelChanged(object? sender, SliderLevelEventArgs e)
        => _ui.Post(() =>
        {
            if (!_disposed) Sliders.UpdateLiveLevel(e.SliderIndex, e.Level);
        });

    private void OnButtonActivity(object? sender, int index)
        => _ui.Post(() =>
        {
            if (!_disposed) _buttonsViewModel?.Pulse(index);
        });

    private void OnActivePresetChanged(object? sender, string presetId)
        => _ui.Post(() =>
        {
            if (!_disposed) _buttonsViewModel?.OnRuntimePresetChanged(presetId);
        });

    /// <summary>A mute flipped (from a button, the Windows mixer, anywhere): light previews follow.</summary>
    private void OnDeviceVolumeChanged(object? sender, DeviceVolumeChangedEventArgs e)
        => _ui.Post(() =>
        {
            if (!_disposed) _buttonsViewModel?.RefreshLights();
        });

    /// <summary>Unsubscribes from the runtime and engine, and disposes the Connection ViewModel.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runtime.DevicesChanged -= OnDevicesChanged;
        _runtime.SliderLevelChanged -= OnSliderLevelChanged;
        _runtime.ButtonActivity -= OnButtonActivity;
        _runtime.ActivePresetChanged -= OnActivePresetChanged;
        _audio.DeviceVolumeChanged -= OnDeviceVolumeChanged;
        _connectionViewModel?.Dispose();
    }
}
