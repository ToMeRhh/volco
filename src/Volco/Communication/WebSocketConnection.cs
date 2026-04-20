using System.Net;
using System.Net.WebSockets;
using Volco.Config;

namespace Volco.Communication;

public class WebSocketConnection : IControllerConnection
{
    private readonly WebSocketConfig _config;
    private readonly ProtocolParser _parser = new();
    private HttpListener? _httpListener;
    private CancellationTokenSource? _cts;
    private int _clientCount;

    public event EventHandler<ControllerMessage>? MessageReceived;
    public event EventHandler<ConnectionStatus>? StatusChanged;
    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;

    public WebSocketConnection(WebSocketConfig config)
    {
        _config = config;
        _parser.MessageParsed += (_, msg) => MessageReceived?.Invoke(this, msg);
    }

    public void Connect()
    {
        try
        {
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add($"http://localhost:{_config.Port}/");
            _httpListener.Start();
            _cts = new CancellationTokenSource();
            // Server is listening but no client yet — remain Disconnected until a client connects
            _ = AcceptLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to start WebSocket server on port {_config.Port}: {ex.Message}");
            SetStatus(ConnectionStatus.Error);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _httpListener != null)
        {
            try
            {
                var context = await _httpListener.GetContextAsync().WaitAsync(ct);
                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    continue;
                }

                var wsContext = await context.AcceptWebSocketAsync(null);
                AppLog.Info("New WebSocket connection accepted");
                _ = HandleClientAsync(wsContext.WebSocket, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    AppLog.Error($"Error in WebSocket accept loop: {ex.Message}");
                    SetStatus(ConnectionStatus.Error);
                }
                break;
            }
        }
    }

    private async Task HandleClientAsync(WebSocket webSocket, CancellationToken ct)
    {
        if (Interlocked.Increment(ref _clientCount) == 1)
            SetStatus(ConnectionStatus.Connected);

        var buffer = new byte[1024];
        try
        {
            while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                if (result.MessageType == WebSocketMessageType.Binary)
                    _parser.FeedBytes(buffer.AsSpan(0, result.Count));
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            AppLog.Error($"WebSocket connection error: {ex.Message}");
        }
        finally
        {
            AppLog.Info("WebSocket connection closed");
            if (Interlocked.Decrement(ref _clientCount) == 0 && !ct.IsCancellationRequested)
                SetStatus(ConnectionStatus.Disconnected);
        }
    }

    private void SetStatus(ConnectionStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    public void Disconnect()
    {
        _cts?.Cancel();
        try
        {
            _httpListener?.Stop();
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }

        _httpListener?.Close();
        _httpListener = null;
        _cts?.Dispose();
        _cts = null;
        _clientCount = 0;
        SetStatus(ConnectionStatus.Disconnected);
    }

    public void Dispose()
    {
        Disconnect();
    }
}
