using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Volco.Audio;
using Volco.Config;
using static Volco.UI.ViewModels.DeviceRefLookup;

namespace Volco.UI.ViewModels;

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
        _config.Config.OutputDevices.Device1 = DeviceRefFromName(value) ?? new DeviceRefConfig();
        Persist();
    }

    partial void OnDevice2Changed(string value)
    {
        _config.Config.OutputDevices.Device2 = DeviceRefFromName(value) ?? new DeviceRefConfig();
        Persist();
    }

    private void Persist() => _config.RequestSave();
}
