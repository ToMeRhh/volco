using CommunityToolkit.Mvvm.ComponentModel;
using Volco.App.Platform;
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
    private readonly IStartWithSystem? _startWithSystem;
    private readonly IAppIcons? _appIcons;
    private readonly AppIconLookup? _appIconLookup;

    // Runtime and engine events fire on arbitrary threads (audio thread, SliderProcessor's
    // coalescing timer, transport threads). Every handler posts through this first.
    private readonly IUiDispatcher _ui;
    private bool _disposed;
    private bool _wasConnected;

    private ConnectionViewModel? _connectionViewModel;
    private SlidersViewModel? _slidersViewModel;
    private ButtonsViewModel? _buttonsViewModel;
    private ControlsViewModel? _controlsViewModel;
    private GeneralViewModel? _generalViewModel;
    private ControllerViewModel? _controllerViewModel;

    /// <summary>The window's one "… · Undo" message.</summary>
    public UndoToastViewModel Toast { get; } = new();

    /// <summary>The version, GitHub link and notices at the bottom of the sidebar.</summary>
    public AboutViewModel About { get; } = new();

    /// <summary>The link status and connection settings: the sidebar footer and the Controller
    /// page's Connection section share it.</summary>
    public ConnectionViewModel Connection =>
        _connectionViewModel ??= new ConnectionViewModel(_config, _runtime, _ui);

    public SlidersViewModel Sliders =>
        _slidersViewModel ??= new SlidersViewModel(_config, _audio, Toast, _appIconLookup);

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
        _buttonsViewModel ??= new ButtonsViewModel(_config, _audio, _runtime, _runtime.Execute, Toast,
                                                   ActionCapabilities.Of(_runtime));

    /// <summary>The Controls page: the drawing and inspector over <see cref="Sliders"/> and
    /// <see cref="Buttons"/>, whose dialogs edit device names through <see cref="Devices"/>.</summary>
    public ControlsViewModel Controls =>
        _controlsViewModel ??= new ControlsViewModel(Sliders, Buttons, _config, Toast, _appIcons, Devices);

    /// <summary>The Controls page's view model if the page was ever opened; null otherwise.</summary>
    public ControlsViewModel? ControlsIfCreated => _controlsViewModel;

    public GeneralViewModel General =>
        _generalViewModel ??= new GeneralViewModel(_config, _startWithSystem);

    public ControllerViewModel Controller =>
        _controllerViewModel ??= new ControllerViewModel(_config, _runtime, _ui, _firmware, Connection);

    /// <summary>The Controller page's view model if the page was ever opened; null otherwise.</summary>
    public ControllerViewModel? ControllerIfCreated => _controllerViewModel;

    /// <summary>The dot on the Controller navigation entry: a firmware update, a controller that
    /// doesn't answer, or one that needs setting up. Read from the runtime's config session, so it
    /// is right before the page is opened.</summary>
    [ObservableProperty]
    private bool _controllerNavDot;

    public SettingsViewModel(ConfigManager config, IAudioEngine audio, VolcoRuntime runtime, IUiDispatcher ui,
                             FirmwareService? firmware = null, IStartWithSystem? startWithSystem = null,
                             IAppIcons? appIcons = null, AppIconLookup? appIconLookup = null)
    {
        _appIconLookup = appIconLookup;
        _firmware = firmware;
        _startWithSystem = startWithSystem;
        _appIcons = appIcons;
        _config = config;
        _audio = audio;
        _runtime = runtime;
        _ui = ui;

        _runtime.DevicesChanged += OnDevicesChanged;
        _runtime.SliderLevelChanged += OnSliderLevelChanged;
        _runtime.ButtonActivity += OnButtonActivity;
        _runtime.ActiveModeChanged += OnActiveModeChanged;
        _runtime.ControllerInfoChanged += OnControllerInfoChanged;
        _runtime.LinkStatusChanged += OnLinkStatusChanged;
        _wasConnected = _runtime.LinkStatus.IsConnected;
        _audio.DeviceVolumeChanged += OnDeviceVolumeChanged;
        _runtime.ControllerConfig.BannerChanged += OnBannerChanged;
        _runtime.ActionStateChanged += OnActionStateChanged;
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

    /// <summary>A nickname or icon changed in the device dialog (UI thread): the cached Controls
    /// page's sliders and buttons re-read their devices and recompute their labels and screen
    /// previews.</summary>
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
            if (_disposed) return;
            if (_controlsViewModel is { } controls) controls.OnSliderLevel(e.SliderIndex, e.Level);
            else Sliders.UpdateLiveLevel(e.SliderIndex, e.Level);
        });

    private void OnButtonActivity(object? sender, int index)
        => _ui.Post(() =>
        {
            if (_disposed) return;
            if (_controlsViewModel is { } controls) controls.OnButtonActivity(index);
            else _buttonsViewModel?.Pulse(index);
        });

    private void OnActiveModeChanged(object? sender, string modeId)
        => _ui.Post(() =>
        {
            if (!_disposed) _buttonsViewModel?.OnRuntimeModeChanged(modeId);
        });

    private void OnControllerInfoChanged(object? sender, EventArgs e)
        => _ui.Post(() =>
        {
            if (_disposed) return;
            _buttonsViewModel?.RefreshController();
            _controlsViewModel?.OnHello();
        });

    /// <summary>Every Hello connects the link, and the Hello's slider sync follows it; the
    /// hardware summary (above) changes only when the controller does.</summary>
    private void OnLinkStatusChanged(object? sender, LinkStatus status)
        => _ui.Post(() =>
        {
            if (_disposed) return;
            var connected = status.IsConnected;
            if (connected && !_wasConnected) _controlsViewModel?.OnHello();
            _wasConnected = connected;
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

    /// <summary>A media session, a watched program or a script run changed: previews and media
    /// pickers follow.</summary>
    private void OnActionStateChanged(object? sender, EventArgs e)
        => _ui.Post(() =>
        {
            if (!_disposed) _buttonsViewModel?.RefreshActionState();
        });

    /// <summary>Unsubscribes from the runtime and engine, and disposes the Connection ViewModel.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runtime.DevicesChanged -= OnDevicesChanged;
        _runtime.SliderLevelChanged -= OnSliderLevelChanged;
        _runtime.ButtonActivity -= OnButtonActivity;
        _runtime.ActiveModeChanged -= OnActiveModeChanged;
        _runtime.ControllerInfoChanged -= OnControllerInfoChanged;
        _runtime.LinkStatusChanged -= OnLinkStatusChanged;
        _audio.DeviceVolumeChanged -= OnDeviceVolumeChanged;
        _runtime.ControllerConfig.BannerChanged -= OnBannerChanged;
        _runtime.ActionStateChanged -= OnActionStateChanged;
        _controllerViewModel?.Dispose();
        if (_devicesViewModel is not null) _devicesViewModel.DeviceSettingsChanged -= OnDeviceSettingsChanged;
        _connectionViewModel?.Dispose();
        _slidersViewModel?.Dispose();
    }
}
