using System.Net;
using System.Net.WebSockets;
using System.Threading.Channels;
using Volco.Core.Config;
using Volco.Core.Diagnostics;

namespace Volco.Core.Communication;

/// <summary>
/// WebSocket server transport. Mirrors <see cref="SerialConnection"/>'s hardening: all mutable
/// state lives under <see cref="_gate"/>, and no lock is held while raising
/// <see cref="StatusChanged"/>, calling into the network or awaiting. Resources are taken out of
/// the fields under the lock and cancelled / aborted / closed outside it. Every
/// <see cref="Connect"/> and teardown bumps <see cref="_generation"/>; the accept loop and the
/// client handlers capture the generation they were started under and raise nothing once it is
/// stale, so a handler that outlives <see cref="Disconnect"/> can never announce into a later
/// connection.
///
/// Event contract: <see cref="Status"/> is authoritative. <see cref="StatusChanged"/> events are
/// hints raised outside the lock by design, so around teardown they can arrive out of order
/// relative to each other and to <see cref="Status"/>; subscribers should re-read
/// <see cref="Status"/> rather than trust event order.
///
/// A WebSocket allows only one outstanding send (on Windows, HttpListener's ServerWebSocket /
/// WebSocketBase throws InvalidOperationException on a second one and then aborts the whole
/// connection; Linux's ManagedWebSocket serialises silently), and the runtime sends several frames
/// back to back, so <see cref="Send"/> only enqueues onto a per-client channel that a single drain loop
/// empties in order, awaiting each send. Queued frames are dropped when the client goes away; the
/// runtime re-pushes full controller state on every connect.
/// </summary>
public class WebSocketConnection : IControllerConnection
{
    // Frames are tiny and the runtime pushes full state on connect, so a stalled client may lose
    // its oldest frames rather than grow the queue without bound.
    private const int SendQueueCapacity = 256;

    private sealed class ClientSession
    {
        public ClientSession(WebSocket ws)
        {
            Ws = ws;
            Queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(SendQueueCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest,
            });
        }

        public WebSocket Ws { get; }
        public Channel<byte[]> Queue { get; }
    }

    private readonly WebSocketConfig _config;
    private readonly ProtocolParser _parser = new();

    private readonly object _gate = new();
    private HttpListener? _httpListener;
    private CancellationTokenSource? _cts;
    private readonly HashSet<ClientSession> _sessions = new();
    private ClientSession? _activeClient;
    private bool _disposed;
    private int _generation;

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
        HttpListener? listener = null;
        var token = CancellationToken.None;
        var generation = 0;
        var raiseError = false;
        lock (_gate)
        {
            if (_disposed || _httpListener != null) return;
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add($"http://localhost:{_config.Port}/");
                listener.Start();
                _httpListener = listener;
                _cts = new CancellationTokenSource();
                token = _cts.Token;
                generation = ++_generation;
            }
            catch (Exception ex)
            {
                AppLog.Error($"Failed to start WebSocket server on port {_config.Port}: {ex.Message}");
                try { listener?.Close(); } catch (Exception) { }
                listener = null;
                _httpListener = null;
                raiseError = Status != ConnectionStatus.Error;
                Status = ConnectionStatus.Error;
            }
        }

        if (listener == null)
        {
            if (raiseError) StatusChanged?.Invoke(this, ConnectionStatus.Error);
            return;
        }

        // Server is listening but no client yet - remain Disconnected until a client connects
        _ = AcceptLoopAsync(listener, token, generation);
    }

    private bool IsCurrent(int generation)
    {
        lock (_gate) return !_disposed && generation == _generation;
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct, int generation)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = await listener.GetContextAsync().WaitAsync(ct);
                try
                {
                    if (!context.Request.IsWebSocketRequest)
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        continue;
                    }

                    var wsContext = await context.AcceptWebSocketAsync(null);
                    AppLog.Info("New WebSocket connection accepted");
                    _ = HandleClientAsync(wsContext.WebSocket, ct, generation);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One bad request must not take the whole server down; only a failure of the
                    // listener itself (GetContextAsync) does.
                    AppLog.Error($"Rejected a WebSocket request: {ex.Message}");
                    try { context.Response.Abort(); } catch (Exception) { }
                    continue;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Stopping the listener during Disconnect() lands here; only a still-current
                // generation is a genuine failure.
                if (!ct.IsCancellationRequested && IsCurrent(generation))
                {
                    AppLog.Error($"Error in WebSocket accept loop: {ex.Message}");
                    Teardown(ConnectionStatus.Error, generation);
                }
                break;
            }
        }
    }

    private async Task HandleClientAsync(WebSocket webSocket, CancellationToken ct, int generation)
    {
        var session = new ClientSession(webSocket);
        ClientSession? previous = null;
        var registered = false;
        var raiseConnected = false;
        lock (_gate)
        {
            if (!_disposed && generation == _generation)
            {
                registered = true;
                previous = _activeClient;
                _activeClient = session;
                _sessions.Add(session);
                if (_sessions.Count == 1 && Status != ConnectionStatus.Connected)
                {
                    Status = ConnectionStatus.Connected;
                    raiseConnected = true;
                }
            }
        }

        if (!registered)
        {
            Abort(webSocket);
            return;
        }

        // Frames queued for the replaced client are meant for a connection that is superseded.
        previous?.Queue.Writer.TryComplete();
        _ = DrainSendQueueAsync(session, ct);

        // _activeClient is set before the status flips, so a subscriber reacting to Connected by
        // calling Send() reaches this client.
        if (raiseConnected) StatusChanged?.Invoke(this, ConnectionStatus.Connected);

        var buffer = new byte[1024];
        try
        {
            while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                    break;
                }
                if (result.MessageType == WebSocketMessageType.Binary)
                    _parser.FeedBytes(buffer.AsSpan(0, result.Count));
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            AppLog.Error($"WebSocket connection error: {ex.Message}");
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            // E.g. a MessageReceived subscriber threw: log it rather than fault this fire-and-forget
            // task unobserved. finally still unwinds the client.
            AppLog.Error($"Unexpected error in WebSocket client handler: {ex}");
            Abort(webSocket);
        }
        finally
        {
            AppLog.Info("WebSocket connection closed");
            var raiseDisconnected = false;
            lock (_gate)
            {
                // A stale handler (Disconnect() already reset the session set) must not touch
                // the state of a newer connection.
                if (generation == _generation && _sessions.Remove(session))
                {
                    if (ReferenceEquals(_activeClient, session)) _activeClient = null;
                    if (!_disposed && _sessions.Count == 0 && Status == ConnectionStatus.Connected)
                    {
                        Status = ConnectionStatus.Disconnected;
                        raiseDisconnected = true;
                    }
                }
            }

            session.Queue.Writer.TryComplete();
            try { webSocket.Dispose(); } catch (Exception) { }

            if (raiseDisconnected) StatusChanged?.Invoke(this, ConnectionStatus.Disconnected);
        }
    }

    /// <summary>The single consumer of a client's send queue: sends one frame at a time, in
    /// order, so there is never more than one outstanding SendAsync on the socket.</summary>
    private static async Task DrainSendQueueAsync(ClientSession session, CancellationToken ct)
    {
        var ws = session.Ws;
        try
        {
            await foreach (var frame in session.Queue.Reader.ReadAllAsync(ct))
            {
                if (ws.State != WebSocketState.Open) break;
                await ws.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Debug($"WebSocket send failed: {ex.Message}");
            // The receive loop notices the dead socket and unwinds the client.
            Abort(ws);
        }
    }

    public void Send(byte[] data)
    {
        ClientSession? session;
        lock (_gate) session = _activeClient;

        // Enqueue only: never blocks the (audio) caller, and the drain loop keeps frames in order.
        session?.Queue.Writer.TryWrite(data);
    }

    private static void Abort(WebSocket ws)
    {
        try { ws.Abort(); } catch (Exception) { }
    }

    /// <summary>Takes ownership of everything running under the lock, then releases it outside
    /// the lock and announces <paramref name="finalStatus"/>. With <paramref name="onlyGeneration"/>
    /// set, does nothing unless that generation is still current (used by the accept loop's own
    /// failure path). Announces when something was running or the status actually changes, so the
    /// first teardown always reports once and repeated ones report nothing; an Error is announced
    /// once per transition.</summary>
    private void Teardown(ConnectionStatus finalStatus, int? onlyGeneration = null)
    {
        HttpListener? listener;
        CancellationTokenSource? cts;
        ClientSession[] sessions;
        bool announce;
        lock (_gate)
        {
            if (onlyGeneration != null && (_disposed || onlyGeneration != _generation)) return;

            listener = _httpListener;
            cts = _cts;
            sessions = _sessions.ToArray();
            var wasRunning = listener != null;
            _httpListener = null;
            _cts = null;
            _sessions.Clear();
            _activeClient = null;
            _generation++;

            announce = finalStatus == ConnectionStatus.Error
                ? Status != ConnectionStatus.Error
                : wasRunning || Status != ConnectionStatus.Disconnected;
            if (announce) Status = finalStatus;
        }

        try { cts?.Cancel(); } catch (ObjectDisposedException) { }

        // Closing the client sockets (not just cancelling the token) is what guarantees no
        // handler is left running against a connection that is gone.
        foreach (var session in sessions)
        {
            session.Queue.Writer.TryComplete();
            Abort(session.Ws);
        }

        try { listener?.Stop(); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        try { listener?.Close(); } catch (ObjectDisposedException) { }

        try { cts?.Dispose(); } catch (ObjectDisposedException) { }

        if (announce) StatusChanged?.Invoke(this, finalStatus);
    }

    public void Disconnect() => Teardown(ConnectionStatus.Disconnected);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Teardown(ConnectionStatus.Disconnected);
    }
}
