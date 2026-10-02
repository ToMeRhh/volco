using System.Net.WebSockets;
using Volco.Core.Communication;
using Volco.Core.Config;
using Xunit;

namespace Volco.Core.Tests.Communication;

public class WebSocketConnectionTests : IDisposable
{
    private readonly WebSocketConnection _connection;
    private readonly int _port;
    private readonly List<ConnectionStatus> _statusChanges = new();
    private readonly List<ControllerMessage> _messages = new();

    public WebSocketConnectionTests()
    {
        _port = Random.Shared.Next(49152, 60000);
        _connection = new WebSocketConnection(new WebSocketConfig { Port = _port });
        _connection.StatusChanged += (_, s) => _statusChanges.Add(s);
        _connection.MessageReceived += (_, m) => _messages.Add(m);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private static byte[] FramePayload(byte[] payload)
    {
        byte checksum = 0;
        foreach (var b in payload)
            checksum ^= b;
        var frame = new byte[payload.Length + 3];
        frame[0] = 0xAA;
        frame[1] = (byte)payload.Length;
        Array.Copy(payload, 0, frame, 2, payload.Length);
        frame[^1] = checksum;
        return frame;
    }

    [Fact]
    public void Connect_StatusRemainsDisconnectedUntilClientConnects()
    {
        _connection.Connect();

        // Server is listening but no client yet — status should be Disconnected
        Assert.Equal(ConnectionStatus.Disconnected, _connection.Status);
        Assert.DoesNotContain(ConnectionStatus.Connected, _statusChanges);
    }

    [Fact]
    public async Task ClientConnect_SetsStatusToConnected()
    {
        _connection.Connect();

        using var client = new ClientWebSocket();
        await ConnectWithRetryAsync(client, new Uri($"ws://localhost:{_port}/"));
        await Task.Delay(50);

        Assert.Equal(ConnectionStatus.Connected, _connection.Status);
        Assert.Contains(ConnectionStatus.Connected, _statusChanges);
    }

    [Fact]
    public async Task ClientDisconnect_SetsStatusToDisconnected()
    {
        _connection.Connect();

        using var client = new ClientWebSocket();
        await ConnectWithRetryAsync(client, new Uri($"ws://localhost:{_port}/"));
        await Task.Delay(50);

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await Task.Delay(100);

        Assert.Equal(ConnectionStatus.Disconnected, _connection.Status);
    }

    [Fact]
    public void Disconnect_SetsStatusToDisconnected()
    {
        _connection.Connect();
        _connection.Disconnect();

        Assert.Equal(ConnectionStatus.Disconnected, _connection.Status);
        Assert.Contains(ConnectionStatus.Disconnected, _statusChanges);
    }

    private async Task ConnectWithRetryAsync(ClientWebSocket client, Uri uri, int maxRetries = 5)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                await client.ConnectAsync(uri, CancellationToken.None);
                return;
            }
            catch (Exception) when (i < maxRetries - 1)
            {
                await Task.Delay(50 * (i + 1));
            }
        }
    }

    [Fact]
    public async Task ReceivesBinaryMessage()
    {
        _connection.Connect();

        using var client = new ClientWebSocket();
        await ConnectWithRetryAsync(client, new Uri($"ws://localhost:{_port}/"));

        var frame = FramePayload(new byte[] { 0x03, 0x00, 0x02, 0x00 }); // slider 0, value 512
        await client.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);

        // Give time for processing
        await Task.Delay(100);

        Assert.Single(_messages);
        var msg = Assert.IsType<SliderMessage>(_messages[0]);
        Assert.Equal(0, msg.SliderIndex);
        Assert.Equal(512, msg.Value);
    }

    [Fact]
    public async Task HandlesMultipleMessages()
    {
        _connection.Connect();

        using var client = new ClientWebSocket();
        await ConnectWithRetryAsync(client, new Uri($"ws://localhost:{_port}/"));

        var frame1 = FramePayload(new byte[] { 0x03, 0x01, 0x01, 0x00 }); // slider 1, value 256
        var frame2 = FramePayload(new byte[] { 0x04, 0x02, 0x01 });       // button 2, pressed
        await client.SendAsync(frame1, WebSocketMessageType.Binary, true, CancellationToken.None);
        await client.SendAsync(frame2, WebSocketMessageType.Binary, true, CancellationToken.None);

        await Task.Delay(100);

        Assert.Equal(2, _messages.Count);
        Assert.IsType<SliderMessage>(_messages[0]);
        Assert.IsType<ButtonMessage>(_messages[1]);
    }

    [Fact]
    public async Task Send_ManyFramesQuickly_AllArriveInOrder()
    {
        _connection.Connect();
        using var client = new ClientWebSocket();
        await ConnectWithRetryAsync(client, new Uri($"ws://localhost:{_port}/"));
        for (int i = 0; i < 100 && _connection.Status != ConnectionStatus.Connected; i++)
            await Task.Delay(20);
        await Task.Delay(50);

        for (byte i = 0; i < 100; i++) _connection.Send(new[] { i });

        var received = new List<byte>();
        var buffer = new byte[256];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (received.Count < 100)
        {
            var r = await client.ReceiveAsync(buffer, cts.Token);
            received.AddRange(buffer.Take(r.Count));
        }
        Assert.Equal(Enumerable.Range(0, 100).Select(i => (byte)i), received);
    }

}
