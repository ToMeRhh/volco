using CommunityToolkit.Mvvm.ComponentModel;
using Volco.Core.Input;
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Firmware;
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
    private readonly FirmwareService? _firmware;

    // Runtime and engine events fire on arbitrary threads (audio thread, SliderProcessor's
    // coalescing timer, transport threads). Every handler posts through this first.
    private readonly IUiDispatcher _ui;
    private bool _disposed;

    private ConnectionViewModel? _connectionViewModel;
    private SlidersViewModel? _slidersViewModel;
    private ButtonsViewModel? _buttonsViewModel;
    private GeneralViewModel? _generalViewModel;
    private ControllerViewModel? _controllerViewModel;

    /// <summary>The window's one "… · Undo" message.</summary>
    public UndoToastViewModel Toast { get; } = new();

    public ConnectionViewModel Connection =>
        _connectionViewModel ??= new ConnectionViewModel(_config, _runtime, _ui);

    public SlidersViewModel Sliders =>
        _slidersViewModel ??= new SlidersViewModel(_config, _audio, Toast);

    public DevicesViewModel Devices
    {
        get
        {
            if (_devicesViewModel is null)
            {
                _devicesViewModel = new DevicesViewModel(_config, _audio);
                _devicesViewModel.DeviceSettingsChanged += OnDeviceSettingsChanged;
            }
            return _devicesViewModel;
        }
    }

    public ButtonsViewModel Buttons =>
        _buttonsViewModel ??= new ButtonsViewModel(_config, _audio, _runtime, _runtime.Execute, Toast);

    public GeneralViewModel General =>
        _generalViewModel ??= new GeneralViewModel(_config);

    public ControllerViewModel Controller =>
        _controllerViewModel ??= new ControllerViewModel(_config, _runtime, _ui, _firmware);

    /// <summary>The Controller page's view model if the page was ever opened; null otherwise.</summary>
    public ControllerViewModel? ControllerIfCreated => _controllerViewModel;

    /// <summary>The dot on the Controller navigation entry: a firmware update, a controller that
    /// doesn't answer, or one that needs setting up. Read from the runtime's config session, so it
    /// is right before the page is opened.</summary>
    [ObservableProperty]
    private bool _controllerNavDot;

    public SettingsViewModel(ConfigManager config, IAudioEngine audio, VolcoRuntime runtime, IUiDispatcher ui,
                             FirmwareService? firmware = null)
    {
        _firmware = firmware;
        _config = config;
        _audio = audio;
        _runtime = runtime;
        _ui = ui;

        _runtime.DevicesChanged += OnDevicesChanged;
        _runtime.SliderLevelChanged += OnSliderLevelChanged;
        _runtime.ButtonActivity += OnButtonActivity;
        _runtime.ActivePresetChanged += OnActivePresetChanged;
        _runtime.ControllerInfoChanged += OnControllerInfoChanged;
        _audio.DeviceVolumeChanged += OnDeviceVolumeChanged;
        _runtime.ControllerConfig.BannerChanged += OnBannerChanged;
        _controllerNavDot = _runtime.ControllerConfig.Banner.NeedsAttention;

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

    /// <summary>A nickname or icon changed on the Devices page (UI thread): the cached Sliders and
    /// Buttons pages re-seed their pickers (so the item templates re-run the label converter) and
    /// recompute card summaries and screen previews.</summary>
    private void OnDeviceSettingsChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        var devices = _audio.GetDevices(AudioFlow.Render);
        _slidersViewModel?.RefreshDevices(devices);
        _buttonsViewModel?.RefreshDevices(devices);
    }

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

    private void OnControllerInfoChanged(object? sender, EventArgs e)
        => _ui.Post(() =>
        {
            if (_disposed) return;
            _buttonsViewModel?.RefreshController();
            _slidersViewModel?.RefreshController();
        });

    private void OnBannerChanged(object? sender, EventArgs e)
        => _ui.Post(() =>
        {
            if (!_disposed) ControllerNavDot = _runtime.ControllerConfig.Banner.NeedsAttention;
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
        _runtime.ControllerInfoChanged -= OnControllerInfoChanged;
        _audio.DeviceVolumeChanged -= OnDeviceVolumeChanged;
        _runtime.ControllerConfig.BannerChanged -= OnBannerChanged;
        _controllerViewModel?.Dispose();
        if (_devicesViewModel is not null) _devicesViewModel.DeviceSettingsChanged -= OnDeviceSettingsChanged;
        _connectionViewModel?.Dispose();
    }
}
