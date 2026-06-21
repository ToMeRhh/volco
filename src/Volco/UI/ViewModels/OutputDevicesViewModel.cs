using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Volco.Audio;
using Volco.Config;

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
    public ObservableCollection<AudioDeviceInfo> OutputDevices { get; } = new();

    public OutputDevicesViewModel(ConfigManager config, IAudioManager audio)
    {
        _config = config;

        var cfg = config.Config.OutputDevices;
        _device1 = cfg.Device1;
        _device2 = cfg.Device2;

        foreach (var d in audio.GetOutputDevices())
            OutputDevices.Add(d);
    }

    partial void OnDevice1Changed(string value)
    {
        _config.Config.OutputDevices.Device1 = value;
        Persist();
    }

    partial void OnDevice2Changed(string value)
    {
        _config.Config.OutputDevices.Device2 = value;
        Persist();
    }

    private void Persist() => _config.RequestSave();
}
