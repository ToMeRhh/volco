using Volco.Core.Communication;
using Volco.Core.Config;
using Xunit;

namespace Volco.Core.Tests.Communication;

/// <summary>
/// Bug 2 (hardware-fix-report): a cleared/blank COM port must never reach SerialPort's
/// constructor and throw out of Connect() — it should fail cleanly with Status.Error instead.
/// </summary>
public class SerialConnectionTests
{
    [Fact]
    public void Connect_NullComPort_SetsErrorStatusWithoutThrowing()
    {
        var connection = new SerialConnection(new SerialConfig { ComPort = null! });
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
        var connection = new SerialConnection(new SerialConfig { ComPort = "" });
        ConnectionStatus? lastStatus = null;
        connection.StatusChanged += (_, s) => lastStatus = s;

        var ex = Record.Exception(() => connection.Connect());

        Assert.Null(ex);
        Assert.Equal(ConnectionStatus.Error, connection.Status);
        Assert.Equal(ConnectionStatus.Error, lastStatus);
    }

    /// <summary>
    /// Hardware-fix (disconnect/reconnect): the blank-port guard in Connect() must return before
    /// any watchdog/reconnect timer is ever created — a config error never spontaneously becomes
    /// valid, so there's nothing to retry.
    /// </summary>
    [Fact]
    public void Connect_BlankComPort_DoesNotStartBackgroundTimers()
    {
        var connection = new SerialConnection(new SerialConfig { ComPort = "" });

        connection.Connect();

        Assert.False(connection.HasBackgroundTimers);
    }

    /// <summary>
    /// Disconnect() must be safe to call on a connection that was never connected (e.g. the
    /// settings UI tearing down before Connect() ever ran), and calling it repeatedly must not
    /// throw or leave any timer running.
    /// </summary>
    [Fact]
    public void Disconnect_BeforeConnect_IsSafeAndIdempotent()
    {
        var connection = new SerialConnection(new SerialConfig { ComPort = "COM99" });

        var ex1 = Record.Exception(() => connection.Disconnect());
        var ex2 = Record.Exception(() => connection.Disconnect());

        Assert.Null(ex1);
        Assert.Null(ex2);
        Assert.Equal(ConnectionStatus.Disconnected, connection.Status);
        Assert.False(connection.HasBackgroundTimers);
    }

    /// <summary>
    /// Dispose() must be safe to call on a connection that was never connected, and safe to call
    /// more than once (App.xaml.cs disposes the old connection before building a new one on every
    /// config change — this must never throw or double-release anything).
    /// </summary>
    [Fact]
    public void Dispose_BeforeConnect_IsSafeAndIdempotent()
    {
        var connection = new SerialConnection(new SerialConfig { ComPort = "COM99" });

        var ex1 = Record.Exception(() => connection.Dispose());
        var ex2 = Record.Exception(() => connection.Dispose());

        Assert.Null(ex1);
        Assert.Null(ex2);
        Assert.Equal(ConnectionStatus.Disconnected, connection.Status);
        Assert.False(connection.HasBackgroundTimers);
    }

    /// <summary>
    /// Dispose() after a failed (blank-port) Connect() must still be safe — Status is Error, not
    /// Disconnected, at that point, and Dispose() should not throw or try to tear down timers
    /// that were correctly never started.
    /// </summary>
    [Fact]
    public void Dispose_AfterBlankPortConnect_IsSafe()
    {
        var connection = new SerialConnection(new SerialConfig { ComPort = null! });
        connection.Connect();

        var ex = Record.Exception(() => connection.Dispose());

        Assert.Null(ex);
        Assert.False(connection.HasBackgroundTimers);
    }

    /// <summary>
    /// StatusChanged must not fire for a status that hasn't actually changed (the settings UI
    /// marshals every event to the UI thread, so a redundant fire is wasted work at best).
    /// Disconnect() on a connection that is already Disconnected is a no-op transition.
    /// </summary>
    [Fact]
    public void Disconnect_WhenAlreadyDisconnected_DoesNotRaiseStatusChanged()
    {
        var connection = new SerialConnection(new SerialConfig { ComPort = "COM99" });
        var raised = false;
        connection.StatusChanged += (_, _) => raised = true;

        connection.Disconnect();

        Assert.False(raised);
        Assert.Equal(ConnectionStatus.Disconnected, connection.Status);
    }
}
