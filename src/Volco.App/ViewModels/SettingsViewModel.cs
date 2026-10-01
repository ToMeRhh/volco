using CommunityToolkit.Mvvm.ComponentModel;
using Volco.Core.Input;
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Runtime;

namespace Volco.App.ViewModels;

/// <summary>
/// Root ViewModel for the Settings window.
/// Lazily creates one instance of each tab ViewModel so state and subscriptions
/// persist across tab switches. Holds the injected services and exposes
/// them to child VMs on demand.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly ConfigManager _config;
    private readonly IAudioEngine _audio;
    private readonly VolcoRuntime _runtime;

    // Runtime events (DevicesChanged, SliderLevelChanged) fire on arbitrary threads (audio thread,
    // SliderProcessor's coalescing timer, transport threads). This VM owns those subscriptions
    // and marshals every handler through the injected dispatcher before touching bound state.
    private readonly IUiDispatcher _ui;
    private bool _disposed;

    // ── Lazy tab ViewModels ───────────────────────────────────────────────────

    private ConnectionViewModel?    _connectionViewModel;
    private SlidersViewModel?       _slidersViewModel;
    private ButtonsViewModel?       _buttonsViewModel;
    private OutputDevicesViewModel? _outputDevicesViewModel;
    private GeneralViewModel?       _generalViewModel;

    public ConnectionViewModel Connection =>
        _connectionViewModel ??= new ConnectionViewModel(_config, _runtime, _ui);

    public SlidersViewModel Sliders =>
        _slidersViewModel ??= new SlidersViewModel(_config, _audio);

    public ButtonsViewModel Buttons =>
        _buttonsViewModel ??= new ButtonsViewModel(_config, _audio);

    public OutputDevicesViewModel OutputDevices =>
        _outputDevicesViewModel ??= new OutputDevicesViewModel(_config, _audio);

    public GeneralViewModel General =>
        _generalViewModel ??= new GeneralViewModel(_config);

    // ── Constructor ───────────────────────────────────────────────────────────

    public SettingsViewModel(ConfigManager config, IAudioEngine audio, VolcoRuntime runtime, IUiDispatcher ui)
    {
        _config     = config;
        _audio      = audio;
        _runtime    = runtime;
        _ui         = ui;

        // Seed the device lookup used to resolve combo-box friendly names to DeviceRefConfig
        // (id + name) BEFORE any child ViewModel is built — Sliders/Buttons construct item
        // ViewModels that call DeviceRefLookup.DeviceRefFromName during their own construction.
        DeviceRefLookup.Devices = audio.GetDevices(AudioFlow.Render);

        _runtime.DevicesChanged     += OnDevicesChanged;
        _runtime.SliderLevelChanged += OnSliderLevelChanged;
    }

    // ── Runtime event handlers (any thread) ───────────────────────────────────

    /// <summary>
    /// <see cref="VolcoRuntime.DevicesChanged"/> fires for every kind of device change
    /// (added/removed/state/default), so a device plugged in or removed after this ViewModel was
    /// created shows up in every open device picker. Marshals to the UI thread before mutating
    /// any child ViewModel's bound state. Only already-constructed tab ViewModels are refreshed
    /// (lazy ??=): an unopened tab builds its device list fresh the first time it is opened.
    /// </summary>
    private void OnDevicesChanged(object? sender, DevicesChangedEventArgs e)
        => _ui.Post(() =>
        {
            if (!_disposed) RefreshDevicesOnUiThread();
        });

    private void OnSliderLevelChanged(object? sender, SliderLevelEventArgs e)
        => _ui.Post(() =>
        {
            if (!_disposed) Sliders.UpdateLiveLevel(e.SliderIndex, e.Level);
        });

    private void RefreshDevicesOnUiThread()
    {
        var devices = _audio.GetDevices(AudioFlow.Render);
        DeviceRefLookup.Devices = devices;
        _slidersViewModel?.RefreshDevices(devices);
        _buttonsViewModel?.RefreshDevices(devices);
        _outputDevicesViewModel?.RefreshDevices(devices);
    }

    /// <summary>Unsubscribes from the runtime and disposes the Connection ViewModel (carried rule d).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runtime.DevicesChanged     -= OnDevicesChanged;
        _runtime.SliderLevelChanged -= OnSliderLevelChanged;
        _connectionViewModel?.Dispose();
    }
}
