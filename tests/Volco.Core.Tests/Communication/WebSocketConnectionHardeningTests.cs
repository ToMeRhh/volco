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

        const int count = 20;
        for (var i = 0; i < count; i++)
            conn.Send(new byte[] { (byte)i });

        var buffer = new byte[16];
        var got = new List<byte>();
        using var cts = new CancellationTokenSource(5000);
        try
        {
            while (got.Count < count)
            {
                var r = await client.ReceiveAsync(buffer, cts.Token);
                Assert.Equal(1, r.Count);
                got.Add(buffer[0]);
            }
        }
        catch (OperationCanceledException) { }

        Assert.Equal(Enumerable.Range(0, count).Select(i => (byte)i), got);
    }
}
