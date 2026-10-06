using Volco.Core.Config;

namespace Volco.Core.Communication;

/// <summary>The transport's own state: a port or socket fact, never "connected" to a controller.
/// That is <see cref="Runtime.LinkState"/>, which also needs the controller's Hello.</summary>
public enum TransportStatus
{
    /// <summary>Nothing open: no transport, the USB port isn't there, or UDP/WebSocket haven't
    /// heard from anyone yet.</summary>
    Closed,
    /// <summary>The USB port is open, a UDP packet arrived, or a WebSocket client connected.</summary>
    Open,
    /// <summary>The port can't be used: another app holds it (or, USB, none is chosen).</summary>
    Failed,
}

public interface IControllerConnection : IDisposable
{
    event EventHandler<ControllerMessage>? MessageReceived;
    event EventHandler<TransportStatus>? StatusChanged;
    TransportStatus Status { get; }
    /// <summary>The address the controller talks from, when the transport knows one (UDP).</summary>
    string? PeerAddress => null;
    void Connect();
    void Disconnect();
    void Send(byte[] data);
}
