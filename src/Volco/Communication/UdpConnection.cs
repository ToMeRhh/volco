using System.Net;
using System.Net.Sockets;
using Volco.Config;

namespace Volco.Communication;

public class UdpConnection : IControllerConnection
{
    private readonly UdpConfig _config;
    private readonly ProtocolParser _parser = new();
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;

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
            Status = ConnectionStatus.Connected;
            StatusChanged?.Invoke(this, Status);
            _ = ReceiveLoopAsync(_cts.Token);
        }
        catch (Exception)
        {
            Status = ConnectionStatus.Error;
            StatusChanged?.Invoke(this, Status);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udpClient != null)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(ct);
                _parser.FeedBytes(result.Buffer);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                Status = ConnectionStatus.Error;
                StatusChanged?.Invoke(this, Status);
                break;
            }
        }
    }

    public void Disconnect()
    {
        _cts?.Cancel();
        _udpClient?.Close();
        _udpClient = null;
        _cts?.Dispose();
        _cts = null;
        Status = ConnectionStatus.Disconnected;
        StatusChanged?.Invoke(this, Status);
    }

    public void Dispose()
    {
        Disconnect();
    }
}
