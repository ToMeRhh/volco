using System.Net;
using System.Net.Sockets;
using Volco.Core.Config;
using Volco.Core.Diagnostics;

namespace Volco.Core.Communication;

/// <summary>
/// Listen-only UDP transport: binds a local port and treats the sender of the latest packet as
/// the controller. "Open" means a packet arrived within the inactivity timeout.
///
/// Mirrors <see cref="SerialConnection"/>'s hardening: all mutable state lives under
/// <see cref="_gate"/>; no lock is held while raising <see cref="StatusChanged"/>, calling into
/// the socket or awaiting. Resources are taken out of the fields under the lock and cancelled /
/// closed / disposed outside it. Every <see cref="Connect"/> and teardown bumps
/// <see cref="_generation"/>; the receive loop and the inactivity timer capture the generation
/// they were started under and do nothing (in particular raise no status) once it is stale, so a
/// callback that outlives <see cref="Disconnect"/> can never announce into a later connection.
///
/// Event contract: <see cref="Status"/> is authoritative. <see cref="StatusChanged"/> events are
/// hints raised outside the lock by design, so around teardown they can arrive out of order
/// relative to each other and to <see cref="Status"/>; subscribers should re-read
/// <see cref="Status"/> rather than trust event order.
/// </summary>
public class UdpConnection : IControllerConnection
{
    private const int DefaultInactivityTimeoutMs = 3000;

    private readonly UdpConfig _config;
    private readonly int _inactivityTimeoutMs;
    private readonly ProtocolParser _parser = new();

    private readonly object _gate = new();
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private System.Threading.Timer? _inactivityTimer;
    private IPEndPoint? _remoteEndPoint;
    private bool _disposed;
    private int _generation;

    public event EventHandler<ControllerMessage>? MessageReceived;
    public event EventHandler<TransportStatus>? StatusChanged;
    public TransportStatus Status { get; private set; } = TransportStatus.Closed;

    /// <summary>The IP the last packet came from, while Open.</summary>
    public string? PeerAddress
    {
        get { lock (_gate) return _remoteEndPoint?.Address.ToString(); }
    }

    public UdpConnection(UdpConfig config) : this(config, DefaultInactivityTimeoutMs)
    {
    }

    /// <summary>Test seam: lets tests shorten the inactivity timeout.</summary>
    internal UdpConnection(UdpConfig config, int inactivityTimeoutMs)
    {
        _config = config;
        _inactivityTimeoutMs = inactivityTimeoutMs;
        _parser.MessageParsed += (_, msg) => MessageReceived?.Invoke(this, msg);
    }

    public void Connect()
    {
        UdpClient? client = null;
        var token = CancellationToken.None;
        var generation = 0;
        var raiseError = false;
        lock (_gate)
        {
            if (_disposed || _udpClient != null) return;
            try
            {
                client = new UdpClient(_config.ListenPort);
                _udpClient = client;
                _cts = new CancellationTokenSource();
                token = _cts.Token;
                generation = ++_generation;
            }
            catch (Exception ex)
            {
                AppLog.Error($"Failed to start UDP listener on port {_config.ListenPort}: {ex.Message}");
                client?.Dispose();
                client = null;
                _udpClient = null;
                raiseError = Status != TransportStatus.Failed;
                Status = TransportStatus.Failed;
            }
        }

        if (client == null)
        {
            if (raiseError) StatusChanged?.Invoke(this, TransportStatus.Failed);
            return;
        }

        // Socket is bound but no client has sent data yet - remain Closed
        _ = ReceiveLoopAsync(client, token, generation);
    }

    private async Task ReceiveLoopAsync(UdpClient client, CancellationToken ct, int generation)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await client.ReceiveAsync(ct);
                if (!OnPacketReceived(result.RemoteEndPoint, generation)) break;
                _parser.FeedBytes(result.Buffer);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // Windows reports an ICMP port-unreachable (the controller endpoint went away) as a
                // ConnectionReset on the next receive. The socket is still fine: keep listening.
                // (Not suppressed via SIO_UDP_CONNRESET: Core stays platform-neutral.)
                AppLog.Debug($"UDP receive: connection reset ignored: {ex.Message}");
            }
            catch (Exception ex)
            {
                // Closing the socket during Disconnect() lands here; only a still-current
                // generation is a genuine failure.
                if (IsCurrent(generation))
                {
                    AppLog.Error($"Error in UDP receive loop: {ex.Message}");
                    Teardown(TransportStatus.Failed, generation);
                }
                break;
            }
        }
    }

    private bool IsCurrent(int generation)
    {
        lock (_gate) return !_disposed && generation == _generation;
    }

    /// <summary>Records the sender, re-arms the inactivity timer and, on the first packet,
    /// raises Open. Returns false if this loop's generation is stale.</summary>
    private bool OnPacketReceived(IPEndPoint remote, int generation)
    {
        var raise = false;
        lock (_gate)
        {
            if (_disposed || generation != _generation) return false;
            _remoteEndPoint = remote;
            if (Status != TransportStatus.Open)
            {
                Status = TransportStatus.Open;
                raise = true;
            }

            if (_inactivityTimer == null)
                _inactivityTimer = new System.Threading.Timer(OnInactivity, generation, _inactivityTimeoutMs, Timeout.Infinite);
            else
                _inactivityTimer.Change(_inactivityTimeoutMs, Timeout.Infinite);
        }

        if (raise) StatusChanged?.Invoke(this, TransportStatus.Open);
        return true;
    }

    /// <summary>Test seam: the generation the current connection (if any) runs under.</summary>
    internal int CurrentGeneration { get { lock (_gate) return _generation; } }

    /// <summary>Inactivity-timer callback; internal so tests can invoke it with a chosen generation.</summary>
    internal void OnInactivity(object? state)
    {
        var generation = (int)state!;
        lock (_gate)
        {
            // A callback already queued when Disconnect() disposed the timer still runs; the
            // generation check is what discards it.
            if (_disposed || generation != _generation || Status != TransportStatus.Open) return;
            Status = TransportStatus.Closed;
        }

        StatusChanged?.Invoke(this, TransportStatus.Closed);
    }

    public void Send(byte[] data)
    {
        UdpClient? client;
        IPEndPoint? remote;
        lock (_gate)
        {
            client = _udpClient;
            remote = _remoteEndPoint;
        }

        if (client == null || remote == null) return;

        try
        {
            client.Send(data, data.Length, remote);
        }
        catch (ObjectDisposedException ex)
        {
            AppLog.Debug($"UDP send dropped (socket closed): {ex.Message}");
        }
        catch (SocketException ex)
        {
            AppLog.Debug($"UDP send failed: {ex.Message}");
        }
    }

    /// <summary>Takes ownership of everything running under the lock, then releases it outside
    /// the lock and announces <paramref name="finalStatus"/>. With <paramref name="onlyGeneration"/>
    /// set, does nothing unless that generation is still current (used by the loop's own failure
    /// path). Announces when something was running or the status actually changes, so the first
    /// teardown always reports once and repeated ones report nothing; a Failed is always announced
    /// once per transition.</summary>
    private void Teardown(TransportStatus finalStatus, int? onlyGeneration = null)
    {
        UdpClient? client;
        CancellationTokenSource? cts;
        System.Threading.Timer? timer;
        bool announce;
        lock (_gate)
        {
            if (onlyGeneration != null && (_disposed || onlyGeneration != _generation)) return;

            client = _udpClient;
            cts = _cts;
            timer = _inactivityTimer;
            var wasRunning = client != null;
            _udpClient = null;
            _cts = null;
            _inactivityTimer = null;
            _remoteEndPoint = null;
            _generation++;

            announce = finalStatus == TransportStatus.Failed
                ? Status != TransportStatus.Failed
                : wasRunning || Status != TransportStatus.Closed;
            if (announce) Status = finalStatus;
        }

        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        try { timer?.Dispose(); } catch (ObjectDisposedException) { }
        try { client?.Close(); } catch (ObjectDisposedException) { }
        try { cts?.Dispose(); } catch (ObjectDisposedException) { }

        if (announce) StatusChanged?.Invoke(this, finalStatus);
    }

    public void Disconnect() => Teardown(TransportStatus.Closed);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Teardown(TransportStatus.Closed);
    }
}
