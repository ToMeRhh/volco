using Volco.Core.Communication;
using Volco.Core.Config;

namespace Volco.Core.Tests.Runtime;

/// <summary>
/// Recording in-memory <see cref="IControllerConnection"/>. Nothing happens on its own: tests
/// drive status and inbound messages with <see cref="SetStatus"/> / <see cref="RaiseMessage"/>.
/// Thread-safe for the runtime's use: <see cref="Send"/> can be called from timer threads while a
/// test reads <see cref="SentFrames"/>.
/// </summary>
public sealed class FakeControllerConnection : IControllerConnection
{
    private readonly object _lock = new();
    private readonly List<byte[]> _sent = new();
    private int _connectCount;
    private int _disposeCount;

    public FakeControllerConnection(ConnectionConfig config) => Config = config;

    /// <summary>The connection config the runtime's factory was called with.</summary>
    public ConnectionConfig Config { get; }

    public event EventHandler<ControllerMessage>? MessageReceived;
    public event EventHandler<TransportStatus>? StatusChanged;

    public TransportStatus Status { get; private set; } = TransportStatus.Closed;

    public int ConnectCount => Volatile.Read(ref _connectCount);
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    /// <summary>Snapshot of every frame passed to <see cref="Send"/>, in order.</summary>
    public IReadOnlyList<byte[]> SentFrames
    {
        get { lock (_lock) return _sent.ToList(); }
    }

    public int SentCount
    {
        get { lock (_lock) return _sent.Count; }
    }

    /// <summary>The status <see cref="Connect"/> moves to (raising <see cref="StatusChanged"/>);
    /// null, the default, leaves it as it is.</summary>
    public TransportStatus? StatusOnConnect { get; set; }

    public void Connect()
    {
        Interlocked.Increment(ref _connectCount);
        if (StatusOnConnect is { } status) SetStatus(status);
    }

    public void Disconnect() { }

    public void Send(byte[] data)
    {
        lock (_lock) _sent.Add(data);
    }

    public void Dispose() => Interlocked.Increment(ref _disposeCount);

    public void RaiseMessage(ControllerMessage message) => MessageReceived?.Invoke(this, message);

    public void SetStatus(TransportStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }
}
