using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Volco.Core.Audio;
using Volco.Core.Config;
using static Volco.App.ViewModels.DeviceRefLookup;

namespace Volco.App.ViewModels;

/// <summary>
/// ViewModel for the Output Devices settings tab.
/// Exposes the two toggle-able output devices and the available device list.
/// </summary>
public partial class OutputDevicesViewModel : ObservableObject
{
    private readonly ConfigManager _config;

    [ObservableProperty]
    private string _device1 = "";

    [ObservableProperty]
    private string _device2 = "";

    /// <summary>All available output devices, for ComboBox binding.</summary>
    public ObservableCollection<AudioDevice> OutputDevices { get; } = new();

    /// <summary>Device names for string-based ComboBox binding.</summary>
    public List<string> OutputDeviceNames { get; private set; }

    public OutputDevicesViewModel(ConfigManager config, IAudioEngine audio)
    {
        _config = config;

        var cfg = config.Config.OutputDevices;
        _device1 = cfg.Device1.FriendlyName;
        _device2 = cfg.Device2.FriendlyName;

        foreach (var d in audio.GetDevices(AudioFlow.Render))
            OutputDevices.Add(d);

        OutputDeviceNames = OutputDevices.Select(d => d.FriendlyName).ToList();
    }

    partial void OnDevice1Changed(string value)
    {
        // Same trap as SliderItemViewModel.OnDeviceNameChanged (Bug 3 refresh) and Bug 2's
        // ComPort refresh: an ItemsSource swap can transiently null this two-way-bound
        // SelectedItem. There's no blank/"None" entry in this picker, so ignore it.
        if (string.IsNullOrEmpty(value)) return;
        _config.Config.OutputDevices.Device1 = DeviceRefFromName(value) ?? new DeviceRefConfig();
        Persist();
    }

    partial void OnDevice2Changed(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        _config.Config.OutputDevices.Device2 = DeviceRefFromName(value) ?? new DeviceRefConfig();
        Persist();
    }

    private void Persist() => _config.RequestSave();

    /// <summary>
    /// Re-seeds the output-device picker from a fresh device snapshot (Bug 3). Callers must
    /// already be on the UI thread — see SliderItemViewModel.RefreshDevices for the same note;
    /// <see cref="IAudioEngine.DevicesChanged"/> fires on the audio thread and is marshalled
    /// once, in SettingsViewModel's DevicesChanged handler.
    /// </summary>
    public void RefreshDevices(IReadOnlyList<AudioDevice> devices)
    {
        OutputDevices.Clear();
        foreach (var d in devices)
            OutputDevices.Add(d);
        OutputDeviceNames = OutputDevices.Select(d => d.FriendlyName).ToList();
        OnPropertyChanged(nameof(OutputDeviceNames));

        // Re-assert current selections against the new ItemsSource without going through the
        // setters/persist path (see RefreshDevices note on SliderItemViewModel).
        OnPropertyChanged(nameof(Device1));
        OnPropertyChanged(nameof(Device2));
    }
}
