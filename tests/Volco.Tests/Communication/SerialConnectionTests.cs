using Volco.Communication;
using Volco.Config;
using Xunit;

namespace Volco.Tests.Communication;

/// <summary>
/// Bug 2 (hardware-fix-report): a cleared/blank COM port must never reach SerialPort's
/// constructor and throw out of Connect() — it should fail cleanly with Status.Error instead.
/// </summary>
public class SerialConnectionTests
{
    [Fact]
    public void Connect_NullComPort_SetsErrorStatusWithoutThrowing()
    {
        var connection = new SerialConnection(new SerialConfig { ComPort = null!, BaudRate = 115200 });
        ConnectionStatus? lastStatus = null;
        connection.StatusChanged += (_, s) => lastStatus = s;

        var ex = Record.Exception(() => connection.Connect());

        Assert.Null(ex);
        Assert.Equal(ConnectionStatus.Error, connection.Status);
        Assert.Equal(ConnectionStatus.Error, lastStatus);
    }

    [Fact]
    public void Connect_EmptyComPort_SetsErrorStatusWithoutThrowing()
    {
        var connection = new SerialConnection(new SerialConfig { ComPort = "", BaudRate = 115200 });
        ConnectionStatus? lastStatus = null;
        connection.StatusChanged += (_, s) => lastStatus = s;

        var ex = Record.Exception(() => connection.Connect());

        Assert.Null(ex);
        Assert.Equal(ConnectionStatus.Error, connection.Status);
        Assert.Equal(ConnectionStatus.Error, lastStatus);
    }
}
