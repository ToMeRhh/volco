using Volco.Core.Config;

namespace Volco.Core.Communication;

public enum ConnectionStatus
{
    Disconnected,
    Connected,
    Error
}

public interface IControllerConnection : IDisposable
{
    event EventHandler<ControllerMessage>? MessageReceived;
    event EventHandler<ConnectionStatus>? StatusChanged;
    ConnectionStatus Status { get; }
    void Connect();
    void Disconnect();
    void Send(byte[] data);
}
