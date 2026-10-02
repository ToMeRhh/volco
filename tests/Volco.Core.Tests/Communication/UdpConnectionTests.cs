using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Volco.Core.Communication;
using Volco.Core.Config;
using Xunit;
using static Volco.Core.Tests.Communication.TransportTestHelpers;

namespace Volco.Core.Tests.Communication;

public class UdpConnectionTests
{
    private sealed class Probe
    {
        public readonly ConcurrentQueue<ConnectionStatus> Statuses = new();
        public int Messages;

        public Probe(UdpConnection conn)
        {
            conn.StatusChanged += (_, s) => Statuses.Enqueue(s);
            conn.MessageReceived += (_, _) => Interlocked.Increment(ref Messages);
        }

        public int Count(ConnectionStatus s) => Statuses.Count(x => x == s);
    }

    private static readonly byte[] SliderFrame = Frame(0x01, 0x00, 0x01, 0x00);

    private static UdpClient NewSender() => new(new IPEndPoint(IPAddress.Loopback, 0));

    private static async Task SendPacketAsync(UdpClient sender, int port)
        => await sender.SendAsync(SliderFrame, SliderFrame.Length, new IPEndPoint(IPAddress.Loopback, port));

    private static bool PortIsBound(int port)
    {
        try
        {
            using var u = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    [Fact]
    public void Connect_AfterDispose_DoesNotBindPort()
    {
        var port = FreeUdpPort();
        var conn = new UdpConnection(new UdpConfig { ListenPort = port });

        conn.Connect();
        Assert.True(PortIsBound(port), "precondition: Connect() must bind the port");

        conn.Dispose();
        Assert.False(PortIsBound(port), "Dispose() must release the port");

        conn.Connect();
        Assert.False(PortIsBound(port), "Connect() after Dispose() must not bind again");
    }

    [Fact]
    public void Dispose_Twice_RaisesDisconnectedOnce()
    {
        var port = FreeUdpPort();
        var conn = new UdpConnection(new UdpConfig { ListenPort = port });
        var probe = new Probe(conn);

        conn.Connect();
        conn.Dispose();
        conn.Dispose();
        conn.Disconnect();

        Assert.Equal(1, probe.Count(ConnectionStatus.Disconnected));
    }

    [Fact]
    public void Dispose_FromManyThreads_DoesNotThrowAndRaisesAtMostOneDisconnected()
    {
        var port = FreeUdpPort();
        var conn = new UdpConnection(new UdpConfig { ListenPort = port });
        var probe = new Probe(conn);
        conn.Connect();

        const int threads = 8;
        using var barrier = new Barrier(threads);
        var errors = new ConcurrentQueue<Exception>();
        var tasks = Enumerable.Range(0, threads).Select(_ => Task.Factory.StartNew(() =>
        {
            try
            {
                barrier.SignalAndWait();
                conn.Dispose();
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        }, TaskCreationOptions.LongRunning)).ToArray();

        Assert.True(Task.WaitAll(tasks, 10000));
        Assert.Empty(errors);
        Assert.True(probe.Count(ConnectionStatus.Disconnected) <= 1);
        Assert.False(PortIsBound(port));
    }

    [Fact]
    public async Task InactivityTimeout_RaisesDisconnected_WhenNoDisconnect()
    {
        // Proves the path the "no status after Disconnect" test relies on can produce the event.
        var port = FreeUdpPort();
        using var conn = new UdpConnection(new UdpConfig { ListenPort = port }, inactivityTimeoutMs: 150);
        var probe = new Probe(conn);
        conn.Connect();

        using var sender = NewSender();
        await SendPacketAsync(sender, port);

        Assert.True(await WaitUntilAsync(() => probe.Count(ConnectionStatus.Connected) == 1));
        Assert.True(await WaitUntilAsync(() => probe.Count(ConnectionStatus.Disconnected) == 1));
    }

    [Fact]
    public async Task Disconnect_SuppressesStaleInactivityCallback()
    {
        const int timeoutMs = 150;
        var port = FreeUdpPort();
        using var conn = new UdpConnection(new UdpConfig { ListenPort = port }, inactivityTimeoutMs: timeoutMs);
        var probe = new Probe(conn);
        conn.Connect();

        using var sender = NewSender();
        await SendPacketAsync(sender, port);
        Assert.True(await WaitUntilAsync(() => probe.Count(ConnectionStatus.Connected) == 1));

        conn.Disconnect();
        Assert.Equal(1, probe.Count(ConnectionStatus.Disconnected));
        var snapshot = probe.Statuses.Count;

        // Wait well past the inactivity timeout: nothing more may be raised.
        await Task.Delay(timeoutMs * 4);
        Assert.Equal(snapshot, probe.Statuses.Count);
        Assert.Equal(ConnectionStatus.Disconnected, conn.Status);
    }

    [Fact]
    public async Task Send_ConcurrentWithDisconnect_DoesNotThrow()
    {
        var port = FreeUdpPort();
        using var conn = new UdpConnection(new UdpConfig { ListenPort = port });
        var probe = new Probe(conn);
        conn.Connect();

        using var sender = NewSender();
        await SendPacketAsync(sender, port);
        Assert.True(await WaitUntilAsync(() => probe.Count(ConnectionStatus.Connected) == 1));

        // Proves Send() reaches the wire before the race starts.
        conn.Send(new byte[] { 1, 2, 3 });
        using (var cts = new CancellationTokenSource(5000))
        {
            var echoed = await sender.ReceiveAsync(cts.Token);
            Assert.Equal(new byte[] { 1, 2, 3 }, echoed.Buffer);
        }

        var stop = false;
        var sends = 0;
        Exception? failure = null;
        var sendThread = new Thread(() =>
        {
            try
            {
                while (!Volatile.Read(ref stop))
                {
                    conn.Send(new byte[] { 9 });
                    Interlocked.Increment(ref sends);
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        sendThread.Start();

        Assert.True(await WaitUntilAsync(() => Volatile.Read(ref sends) > 50));
        conn.Disconnect();
        var after = Volatile.Read(ref sends);
        Assert.True(await WaitUntilAsync(() => Volatile.Read(ref sends) > after + 50));
        Volatile.Write(ref stop, true);
        Assert.True(sendThread.Join(5000));

        Assert.Null(failure);
    }

    [Fact]
    public async Task Reconnect_OnSamePort_ReceivesAgain()
    {
        var port = FreeUdpPort();
        using var conn = new UdpConnection(new UdpConfig { ListenPort = port });
        var probe = new Probe(conn);

        conn.Connect();
        using var sender = NewSender();
        await SendPacketAsync(sender, port);
        Assert.True(await WaitUntilAsync(() => Volatile.Read(ref probe.Messages) == 1));

        conn.Disconnect();
        Assert.Equal(ConnectionStatus.Disconnected, conn.Status);
        Assert.False(PortIsBound(port));

        conn.Connect();
        Assert.True(PortIsBound(port));
        await SendPacketAsync(sender, port);
        Assert.True(await WaitUntilAsync(() => Volatile.Read(ref probe.Messages) == 2));
        Assert.Equal(ConnectionStatus.Connected, conn.Status);
    }

    [Fact]
    public async Task StaleInactivityCallback_FromPreviousConnection_IsIgnored()
    {
        // Deterministic A4 witness: invoke the timer callback by hand with a chosen generation.
        // The real timer is made too slow to interfere.
        var port = FreeUdpPort();
        using var conn = new UdpConnection(new UdpConfig { ListenPort = port }, inactivityTimeoutMs: 600000);
        var probe = new Probe(conn);
        using var sender = NewSender();

        conn.Connect();
        await SendPacketAsync(sender, port);
        Assert.True(await WaitUntilAsync(() => probe.Count(ConnectionStatus.Connected) == 1));
        var oldGeneration = conn.CurrentGeneration;

        conn.Disconnect();
        conn.Connect();
        await SendPacketAsync(sender, port);
        Assert.True(await WaitUntilAsync(() => probe.Count(ConnectionStatus.Connected) == 2));
        var currentGeneration = conn.CurrentGeneration;
        Assert.NotEqual(oldGeneration, currentGeneration);
        var disconnectedBefore = probe.Count(ConnectionStatus.Disconnected);

        conn.OnInactivity(oldGeneration);
        Assert.Equal(ConnectionStatus.Connected, conn.Status);
        Assert.Equal(disconnectedBefore, probe.Count(ConnectionStatus.Disconnected));

        // Proves the callback path does fire for the current generation.
        conn.OnInactivity(currentGeneration);
        Assert.Equal(ConnectionStatus.Disconnected, conn.Status);
        Assert.Equal(disconnectedBefore + 1, probe.Count(ConnectionStatus.Disconnected));
    }
}
