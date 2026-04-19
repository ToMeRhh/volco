using System.IO.Ports;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Volco.Config;

namespace Volco.UI;

public sealed partial class ConnectionTab : Page
{
    private ConfigManager? _configManager;
    private bool _isLoading = true;

    public ConnectionTab()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _configManager = e.Parameter as ConfigManager;
        if (_configManager == null) return;

        _isLoading = true;
        LoadComPorts();
        LoadConfig();
        _isLoading = false;
    }

    private void LoadComPorts()
    {
        ComPortCombo.Items.Clear();
        foreach (var port in SerialPort.GetPortNames())
        {
            ComPortCombo.Items.Add(new ComboBoxItem { Content = port, Tag = port });
        }
    }

    private void LoadConfig()
    {
        var config = _configManager!.Config.Connection;

        // Set connection type radio
        ConnectionTypeRadio.SelectedIndex = config.Type == ConnectionType.Serial ? 0 : 1;

        // Set COM port
        for (int i = 0; i < ComPortCombo.Items.Count; i++)
        {
            if ((ComPortCombo.Items[i] as ComboBoxItem)?.Tag?.ToString() == config.Serial.ComPort)
            {
                ComPortCombo.SelectedIndex = i;
                break;
            }
        }

        // Set baud rate
        for (int i = 0; i < BaudRateCombo.Items.Count; i++)
        {
            if ((BaudRateCombo.Items[i] as ComboBoxItem)?.Tag?.ToString() == config.Serial.BaudRate.ToString())
            {
                BaudRateCombo.SelectedIndex = i;
                break;
            }
        }

        UdpPortBox.Value = config.Udp.ListenPort;
        UpdatePanelVisibility();
    }

    private void ConnectionType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _configManager == null) return;
        var selected = ConnectionTypeRadio.SelectedIndex;
        _configManager.Config.Connection.Type = selected == 0 ? ConnectionType.Serial : ConnectionType.Udp;
        UpdatePanelVisibility();
        _configManager.Save();
    }

    private void UpdatePanelVisibility()
    {
        var isSerial = _configManager?.Config.Connection.Type == ConnectionType.Serial;
        SerialPanel.Visibility = isSerial ? Visibility.Visible : Visibility.Collapsed;
        UdpPanel.Visibility = isSerial ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SerialSetting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _configManager == null) return;
        if (ComPortCombo.SelectedItem is ComboBoxItem portItem)
            _configManager.Config.Connection.Serial.ComPort = portItem.Tag?.ToString() ?? "";
        if (BaudRateCombo.SelectedItem is ComboBoxItem baudItem)
            _configManager.Config.Connection.Serial.BaudRate = int.Parse(baudItem.Tag?.ToString() ?? "9600");
        _configManager.Save();
    }

    private void UdpPort_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isLoading || _configManager == null) return;
        _configManager.Config.Connection.Udp.ListenPort = (int)args.NewValue;
        _configManager.Save();
    }
}
