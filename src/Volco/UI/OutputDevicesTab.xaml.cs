using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Volco.Audio;
using Volco.Config;

namespace Volco.UI;

public sealed partial class OutputDevicesTab : Page
{
    private ConfigManager? _configManager;
    private IAudioManager? _audioManager;
    private bool _isLoading = true;

    public OutputDevicesTab()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is ValueTuple<ConfigManager, IAudioManager> tuple)
        {
            _configManager = tuple.Item1;
            _audioManager = tuple.Item2;
        }
        if (_configManager == null || _audioManager == null) return;

        _isLoading = true;
        LoadDevices();
        _isLoading = false;
    }

    private void LoadDevices()
    {
        var devices = _audioManager!.GetOutputDevices();
        var config = _configManager!.Config.OutputDevices;

        PopulateCombo(Device1Combo, devices, config.Device1);
        PopulateCombo(Device2Combo, devices, config.Device2);
    }

    private static void PopulateCombo(ComboBox combo, List<AudioDeviceInfo> devices, string selectedName)
    {
        combo.Items.Clear();
        foreach (var device in devices)
        {
            var item = new ComboBoxItem { Content = device.Name, Tag = device.Name };
            combo.Items.Add(item);
            if (device.Name == selectedName)
                combo.SelectedItem = item;
        }
    }

    private void Device1_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _configManager == null) return;
        if (Device1Combo.SelectedItem is ComboBoxItem item && item.Tag is string name)
        {
            _configManager.Config.OutputDevices.Device1 = name;
            _configManager.Save();
        }
    }

    private void Device2_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _configManager == null) return;
        if (Device2Combo.SelectedItem is ComboBoxItem item && item.Tag is string name)
        {
            _configManager.Config.OutputDevices.Device2 = name;
            _configManager.Save();
        }
    }
}
