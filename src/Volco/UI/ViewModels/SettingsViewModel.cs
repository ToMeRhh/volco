using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Volco.Audio;
using Volco.Communication;
using Volco.Config;

namespace Volco.UI.ViewModels;

/// <summary>
/// Root ViewModel for the Settings window.
/// Lazily creates one instance of each tab ViewModel so state and subscriptions
/// persist across tab switches. Holds the three injected services and exposes
/// them to child VMs on demand.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ConfigManager _config;
    private readonly IAudioEngine _audio;
    private IControllerConnection? _connection;

    // Captured on construction (UI thread, same as ConnectionViewModel._dispatcher) so
    // RefreshDevices — invoked by App off IAudioEngine.DevicesChanged, which fires on the audio
    // thread — can marshal safely before touching any child ViewModel's bound state.
    private readonly DispatcherQueue? _dispatcher;

    // ── Lazy tab ViewModels ───────────────────────────────────────────────────

    private ConnectionViewModel?    _connectionViewModel;
    private SlidersViewModel?       _slidersViewModel;
    private ButtonsViewModel?       _buttonsViewModel;
    private OutputDevicesViewModel? _outputDevicesViewModel;
    private GeneralViewModel?       _generalViewModel;

    public ConnectionViewModel Connection =>
        _connectionViewModel ??= new ConnectionViewModel(_config, _connection);

    public SlidersViewModel Sliders =>
        _slidersViewModel ??= new SlidersViewModel(_config, _audio);

    public ButtonsViewModel Buttons =>
        _buttonsViewModel ??= new ButtonsViewModel(_config, _audio);

    public OutputDevicesViewModel OutputDevices =>
        _outputDevicesViewModel ??= new OutputDevicesViewModel(_config, _audio);

    public GeneralViewModel General =>
        _generalViewModel ??= new GeneralViewModel(_config);

    // ── Constructor ───────────────────────────────────────────────────────────

    public SettingsViewModel(ConfigManager config, IAudioEngine audio, IControllerConnection? connection)
    {
        _config     = config;
        _audio      = audio;
        _connection = connection;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // Seed the device lookup used to resolve combo-box friendly names to DeviceRefConfig
        // (id + name) BEFORE any child ViewModel is built — Sliders/Buttons construct item
        // ViewModels that call DeviceRefLookup.DeviceRefFromName during their own construction.
        DeviceRefLookup.Devices = audio.GetDevices(AudioFlow.Render);
    }

    // ── Device-list refresh (Bug 3) ─────────────────────────────────────────────

    /// <summary>
    /// Called by App when <see cref="IAudioEngine.DevicesChanged"/> fires, so a device plugged
    /// in (or removed) after this settings window was first opened shows up without requiring a
    /// restart. DeviceRefLookup.Devices was otherwise only ever seeded once, in the constructor
    /// above, and each tab's OutputDeviceNames/OutputDevices only ever built once, in its own
    /// constructor — so nothing previously refreshed either. DevicesChanged is raised from
    /// Volco.Audio.Windows.AudioEngine's dedicated audio thread, so this marshals via the
    /// dispatcher captured above before mutating any child ViewModel's bound state, the same
    /// pattern ConnectionViewModel already uses for its own background-thread callback.
    /// Only already-constructed tab ViewModels are refreshed (lazy ??=): an unopened tab builds
    /// its device list fresh, from the current engine snapshot, whenever it's first opened.
    /// </summary>
    public void RefreshDevices()
    {
        if (_dispatcher != null)
            _dispatcher.TryEnqueue(RefreshDevicesOnUiThread);
        else
            RefreshDevicesOnUiThread();
    }

    private void RefreshDevicesOnUiThread()
    {
        var devices = _audio.GetDevices(AudioFlow.Render);
        DeviceRefLookup.Devices = devices;
        _slidersViewModel?.RefreshDevices(devices);
        _buttonsViewModel?.RefreshDevices(devices);
        _outputDevicesViewModel?.RefreshDevices(devices);
    }

    // ── Connection-object hot-swap ─────────────────────────────────────────────

    /// <summary>
    /// Called by App when the hardware transport is restarted after a connection
    /// config change. Forwards the new connection object to the ConnectionViewModel
    /// if it has already been created.
    /// </summary>
    public void UpdateConnection(IControllerConnection? newConnection)
    {
        _connection = newConnection;
        _connectionViewModel?.UpdateConnection(newConnection);
    }
}
