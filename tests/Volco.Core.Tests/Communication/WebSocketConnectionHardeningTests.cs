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
}
