using System.Net;
using System.Net.Sockets;
using Volco.Config;

namespace Volco.Communication;

public class UdpConnection : IControllerConnection
{
    private const int InactivityTimeoutMs = 3000;

    private readonly UdpConfig _config;
    private readonly ProtocolParser _parser = new();
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private System.Threading.Timer? _inactivityTimer;

    public event EventHandler<ControllerMessage>? MessageReceived;
    public event EventHandler<ConnectionStatus>? StatusChanged;
    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;

    public UdpConnection(UdpConfig config)
    {
        _config = config;
        _parser.MessageParsed += (_, msg) => MessageReceived?.Invoke(this, msg);
    }

    public void Connect()
    {
        try
        {
            _udpClient = new UdpClient(_config.ListenPort);
            _cts = new CancellationTokenSource();
            // Socket is bound but no client has sent data yet — remain Disconnected
            _ = ReceiveLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to start UDP listener on port {_config.ListenPort}: {ex.Message}");
            SetStatus(ConnectionStatus.Error);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udpClient != null)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(ct);
                OnPacketReceived();
                _parser.FeedBytes(result.Buffer);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLog.Error($"Error in UDP receive loop: {ex.Message}");
                SetStatus(ConnectionStatus.Error);
                break;
            }
        }
    }

    private void OnPacketReceived()
    {
        if (Status != ConnectionStatus.Connected)
            SetStatus(ConnectionStatus.Connected);

        // Reset inactivity timer on every packet
        _inactivityTimer?.Change(InactivityTimeoutMs, Timeout.Infinite);
        if (_inactivityTimer == null)
            _inactivityTimer = new System.Threading.Timer(_ => SetStatus(ConnectionStatus.Disconnected),
                null, InactivityTimeoutMs, Timeout.Infinite);
    }

    private void SetStatus(ConnectionStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    public void Disconnect()
    {
        _cts?.Cancel();
        _inactivityTimer?.Dispose();
        _inactivityTimer = null;
        _udpClient?.Close();
        _udpClient = null;
        _cts?.Dispose();
        _cts = null;
        SetStatus(ConnectionStatus.Disconnected);
    }

    public void Dispose()
    {
        Disconnect();
    }
}
