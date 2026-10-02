using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Volco.Core.Communication;
using Volco.Core.Config;
using Xunit;
using static Volco.Core.Tests.Communication.TransportTestHelpers;

namespace Volco.Core.Tests.Communication;

public class WebSocketConnectionHardeningTests
{
    private static async Task<ClientWebSocket> ConnectClientAsync(int port)
    {
        for (var i = 0; ; i++)
        {
            var client = new ClientWebSocket();
            try
            {
                using var cts = new CancellationTokenSource(5000);
                await client.ConnectAsync(new Uri($"ws://localhost:{port}/"), cts.Token);
                return client;
            }
            catch when (i < 20)
            {
                client.Dispose();
                await Task.Delay(50);
            }
        }
    }

    [Fact]
    public async Task Send_Burst_DeliversAllFramesInOrder()
    {
        var port = FreeTcpPort();
        using var conn = new WebSocketConnection(new WebSocketConfig { Port = port });
        var received = 0;
        conn.MessageReceived += (_, _) => Interlocked.Increment(ref received);
        conn.Connect();

        using var client = await ConnectClientAsync(port);
        // A round trip proves the server has registered this client as the active one.
        await client.SendAsync(Frame(0x02, 0x00, 0x01), WebSocketMessageType.Binary, true, CancellationToken.None);
        Assert.True(await WaitUntilAsync(() => Volatile.Read(ref received) == 1), "server never saw the client's frame");

        // Frames are large and the client does not read until every Send() has returned, so the
        // socket's send buffer fills and a SendAsync stays outstanding while the next Send() is
        // issued - the situation that makes a fire-and-forget SendAsync throw (a WebSocket
        // allows one outstanding send). Tiny frames would complete synchronously and hide it.
        const int count = 20;
        const int frameSize = 512 * 1024;
        for (var i = 0; i < count; i++)
        {
            var frame = new byte[frameSize];
            frame[0] = (byte)i;
            conn.Send(frame);
        }

        var buffer = new byte[64 * 1024];
        var got = new List<byte>();
        using var cts = new CancellationTokenSource(10000);
        try
        {
            while (got.Count < count)
            {
                var total = 0;
                var first = (byte)0;
                ValueWebSocketReceiveResult r;
                do
                {
                    r = await client.ReceiveAsync(buffer.AsMemory(), cts.Token);
                    if (total == 0) first = buffer[0];
                    total += r.Count;
                } while (!r.EndOfMessage);
                Assert.Equal(frameSize, total);
                got.Add(first);
            }
        }
        catch (OperationCanceledException) { }

        Assert.Equal(Enumerable.Range(0, count).Select(i => (byte)i), got);
    }

    /// <summary>A ClientWebSocket only learns the server went away by reading; read until it does.</summary>
    private static async Task<bool> WaitUntilClosedAsync(ClientWebSocket client)
    {
        using var cts = new CancellationTokenSource(5000);
        var buffer = new byte[64];
        try
        {
            while (client.State == WebSocketState.Open)
                await client.ReceiveAsync(buffer, cts.Token);
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        return client.State != WebSocketState.Open;
    }

    private sealed class Probe
    {
        public readonly ConcurrentQueue<ConnectionStatus> Statuses = new();

        public Probe(WebSocketConnection conn) => conn.StatusChanged += (_, s) => Statuses.Enqueue(s);

        public int Count(ConnectionStatus s) => Statuses.Count(x => x == s);
    }

    private static async Task<bool> CanConnectTcpAsync(int port)
    {
        using var tcp = new TcpClient();
        try
        {
            using var cts = new CancellationTokenSource(2000);
            await tcp.ConnectAsync("localhost", port, cts.Token);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [Fact]
    public async Task Connect_AfterDispose_DoesNotListenAgain()
    {
        var port = FreeTcpPort();
        var conn = new WebSocketConnection(new WebSocketConfig { Port = port });

        conn.Connect();
        Assert.True(await WaitUntilAsync(() => CanConnectTcpAsync(port).GetAwaiter().GetResult()),
            "precondition: Connect() must start listening");

        conn.Dispose();
        Assert.True(await WaitUntilAsync(() => !CanConnectTcpAsync(port).GetAwaiter().GetResult()),
            "Dispose() must stop listening");

        conn.Connect();
        Assert.False(await CanConnectTcpAsync(port), "Connect() after Dispose() must not listen again");
    }

    [Fact]
    public async Task Dispose_Twice_RaisesDisconnectedOnce()
    {
        var port = FreeTcpPort();
        var conn = new WebSocketConnection(new WebSocketConfig { Port = port });
        var probe = new Probe(conn);

        conn.Connect();
        using var client = await ConnectClientAsync(port);
        Assert.True(await WaitUntilAsync(() => conn.Status == ConnectionStatus.Connected));

        conn.Dispose();
        conn.Dispose();
        conn.Disconnect();

        Assert.Equal(1, probe.Count(ConnectionStatus.Disconnected));
        // The client handler unwinding afterwards must not add a second one.
        Assert.True(await WaitUntilClosedAsync(client));
        await Task.Delay(200);
        Assert.Equal(1, probe.Count(ConnectionStatus.Disconnected));
    }

    [Fact]
    public async Task Dispose_FromManyThreads_DoesNotThrowAndRaisesAtMostOneDisconnected()
    {
        var port = FreeTcpPort();
        var conn = new WebSocketConnection(new WebSocketConfig { Port = port });
        var probe = new Probe(conn);
        conn.Connect();
        using var client = await ConnectClientAsync(port);
        Assert.True(await WaitUntilAsync(() => conn.Status == ConnectionStatus.Connected));

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
    }

    [Fact]
    public async Task Disconnect_ClosesClient_AndRaisesNothingAfterDisconnected()
    {
        var port = FreeTcpPort();
        using var conn = new WebSocketConnection(new WebSocketConfig { Port = port });
        var probe = new Probe(conn);
        conn.Connect();
        using var client = await ConnectClientAsync(port);
        Assert.True(await WaitUntilAsync(() => conn.Status == ConnectionStatus.Connected));

        conn.Disconnect();
        Assert.Equal(1, probe.Count(ConnectionStatus.Disconnected));

        // The client socket is closed by Disconnect() itself (not only the token cancelled).
        using var cts = new CancellationTokenSource(5000);
        try { await client.ReceiveAsync(new byte[16], cts.Token); }
        catch (WebSocketException) { }
        Assert.NotEqual(WebSocketState.Open, client.State);

        var snapshot = probe.Statuses.ToArray();
        await Task.Delay(300);
        Assert.Equal(snapshot, probe.Statuses.ToArray());
        Assert.Equal(ConnectionStatus.Disconnected, conn.Status);
    }

    [Fact]
    public async Task Reconnect_OnSamePort_ServesNewClientAndStaleClientRaisesNothing()
    {
        var port = FreeTcpPort();
        using var conn = new WebSocketConnection(new WebSocketConfig { Port = port });
        var probe = new Probe(conn);
        var received = 0;
        conn.MessageReceived += (_, _) => Interlocked.Increment(ref received);

        conn.Connect();
        using var first = await ConnectClientAsync(port);
        Assert.True(await WaitUntilAsync(() => conn.Status == ConnectionStatus.Connected));

        conn.Disconnect();
        conn.Connect();

        using var second = await ConnectClientAsync(port);
        await second.SendAsync(Frame(0x02, 0x00, 0x01), WebSocketMessageType.Binary, true, CancellationToken.None);
        Assert.True(await WaitUntilAsync(() => Volatile.Read(ref received) == 1));
        Assert.Equal(ConnectionStatus.Connected, conn.Status);

        // The first client's handler unwinding must not flip the new connection to Disconnected.
        Assert.True(await WaitUntilClosedAsync(first));
        await Task.Delay(300);
        Assert.Equal(ConnectionStatus.Connected, conn.Status);
        Assert.Equal(1, probe.Count(ConnectionStatus.Disconnected));

        conn.Send(new byte[] { 0x42 });
        var buffer = new byte[8];
        using var cts = new CancellationTokenSource(5000);
        var r = await second.ReceiveAsync(buffer, cts.Token);
        Assert.Equal(1, r.Count);
        Assert.Equal(0x42, buffer[0]);
    }
}
