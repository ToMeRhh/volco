using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
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
    private readonly IAudioManager _audio;
    private IControllerConnection? _connection;

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

    public SettingsViewModel(ConfigManager config, IAudioManager audio, IControllerConnection? connection)
    {
        _config     = config;
        _audio      = audio;
        _connection = connection;

        // Seed the device lookup used to resolve combo-box friendly names to DeviceRefConfig
        // (id + name) BEFORE any child ViewModel is built — Sliders/Buttons construct item
        // ViewModels that call DeviceRefLookup.DeviceRefFromName during their own construction.
        DeviceRefLookup.Devices = audio.GetOutputDevices()
            .Select(d => new AudioDevice(d.Id, d.Name, AudioFlow.Render, false, false, false))
            .ToList();
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
