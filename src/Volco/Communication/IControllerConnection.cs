using Volco.Config;

namespace Volco.Communication;

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
}
