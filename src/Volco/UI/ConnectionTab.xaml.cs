using System.IO.Ports;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Volco.Communication;
using Volco.Config;

namespace Volco.UI;

public sealed partial class ConnectionTab : Page
{
    private ConfigManager? _configManager;
    private IControllerConnection? _connection;
    private bool _isLoading = true;

    public ConnectionTab()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is (ConfigManager cm, IControllerConnection? conn))
        {
            _configManager = cm;
            _connection = conn;
        }
        else if (e.Parameter is ConfigManager cmOnly)
        {
            _configManager = cmOnly;
        }

        if (_configManager == null) return;

        _isLoading = true;
        LoadComPorts();
        LoadConfig();
        _isLoading = false;

        _configManager.ConfigChanged += OnConfigChanged;
        SubscribeToConnection();
        UpdateStatusIndicator(_connection?.Status ?? ConnectionStatus.Disconnected);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        UnsubscribeFromConnection();
        if (_configManager != null)
            _configManager.ConfigChanged -= OnConfigChanged;
    }

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UnsubscribeFromConnection();
            _connection = ((App)Application.Current).Connection;
            SubscribeToConnection();
            UpdateStatusIndicator(_connection?.Status ?? ConnectionStatus.Disconnected);
        });
    }

    private void SubscribeToConnection()
    {
        if (_connection != null)
            _connection.StatusChanged += OnConnectionStatusChanged;
    }

    private void UnsubscribeFromConnection()
    {
        if (_connection != null)
            _connection.StatusChanged -= OnConnectionStatusChanged;
    }

    private void OnConnectionStatusChanged(object? sender, ConnectionStatus status)
    {
        DispatcherQueue.TryEnqueue(() => UpdateStatusIndicator(status));
    }

    private void UpdateStatusIndicator(ConnectionStatus status)
    {
        switch (status)
        {
            case ConnectionStatus.Connected:
                StatusIndicator.Fill = new SolidColorBrush(Colors.Green);
                StatusText.Text = "Connected";
                break;
            case ConnectionStatus.Disconnected:
                StatusIndicator.Fill = new SolidColorBrush(Colors.Gray);
                StatusText.Text = "Disconnected";
                break;
            case ConnectionStatus.Error:
                StatusIndicator.Fill = new SolidColorBrush(Colors.Red);
                StatusText.Text = "Error";
                break;
        }
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
        ConnectionTypeRadio.SelectedIndex = config.Type switch
        {
            ConnectionType.Serial => 0,
            ConnectionType.Udp => 1,
            ConnectionType.WebSocket => 2,
            _ => 0
        };

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
        WebSocketPortBox.Value = config.WebSocket.Port;
        UpdatePanelVisibility();
    }

    private void ConnectionType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _configManager == null) return;
        _configManager.Config.Connection.Type = ConnectionTypeRadio.SelectedIndex switch
        {
            0 => ConnectionType.Serial,
            1 => ConnectionType.Udp,
            2 => ConnectionType.WebSocket,
            _ => ConnectionType.Serial
        };
        UpdatePanelVisibility();
        _configManager.Save();
    }

    private void UpdatePanelVisibility()
    {
        var type = _configManager?.Config.Connection.Type;
        SerialPanel.Visibility = type == ConnectionType.Serial ? Visibility.Visible : Visibility.Collapsed;
        UdpPanel.Visibility = type == ConnectionType.Udp ? Visibility.Visible : Visibility.Collapsed;
        WebSocketPanel.Visibility = type == ConnectionType.WebSocket ? Visibility.Visible : Visibility.Collapsed;
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

    private void WebSocketPort_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isLoading || _configManager == null) return;
        _configManager.Config.Connection.WebSocket.Port = (int)args.NewValue;
        _configManager.Save();
    }
}
