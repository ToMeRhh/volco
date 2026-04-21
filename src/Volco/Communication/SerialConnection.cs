using System.IO.Ports;
using Volco.Config;

namespace Volco.Communication;

public class SerialConnection : IControllerConnection
{
    private readonly SerialConfig _config;
    private readonly ProtocolParser _parser = new();
    private SerialPort? _serialPort;

    public event EventHandler<ControllerMessage>? MessageReceived;
    public event EventHandler<ConnectionStatus>? StatusChanged;
    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;

    public SerialConnection(SerialConfig config)
    {
        _config = config;
        _parser.MessageParsed += (_, msg) => MessageReceived?.Invoke(this, msg);
    }

    public void Connect()
    {
        try
        {
            _serialPort = new SerialPort(_config.ComPort, _config.BaudRate);
            _serialPort.DataReceived += OnDataReceived;
            _serialPort.Open();
            Status = ConnectionStatus.Connected;
            StatusChanged?.Invoke(this, Status);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to open serial port {_config.ComPort}: {ex.Message}");
            Status = ConnectionStatus.Error;
            StatusChanged?.Invoke(this, Status);
        }
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_serialPort == null || !_serialPort.IsOpen)
            return;

        var bytesToRead = _serialPort.BytesToRead;
        var buffer = new byte[bytesToRead];
        _serialPort.Read(buffer, 0, bytesToRead);
        _parser.FeedBytes(buffer);
    }

    public void Send(byte[] data)
    {
        if (_serialPort is { IsOpen: true })
            _serialPort.Write(data, 0, data.Length);
    }

    public void Disconnect()
    {
        if (_serialPort is { IsOpen: true })
        {
            _serialPort.DataReceived -= OnDataReceived;
            _serialPort.Close();
        }
        _serialPort = null;
        Status = ConnectionStatus.Disconnected;
        StatusChanged?.Invoke(this, Status);
    }

    public void Dispose()
    {
        Disconnect();
        _serialPort?.Dispose();
    }
}
