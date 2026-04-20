using System.Net.WebSockets;
using Volco.Communication;
using Volco.Config;
using Xunit;

namespace Volco.Tests.Communication;

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
    public void Connect_SetsStatusToConnected()
    {
        _connection.Connect();

        Assert.Equal(ConnectionStatus.Connected, _connection.Status);
        Assert.Contains(ConnectionStatus.Connected, _statusChanges);
    }

    [Fact]
    public void Disconnect_SetsStatusToDisconnected()
    {
        _connection.Connect();
        _connection.Disconnect();

        Assert.Equal(ConnectionStatus.Disconnected, _connection.Status);
        Assert.Contains(ConnectionStatus.Disconnected, _statusChanges);
    }

    [Fact]
    public async Task ReceivesBinaryMessage()
    {
        _connection.Connect();

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://localhost:{_port}/"), CancellationToken.None);

        var frame = FramePayload(new byte[] { 0x01, 0x00, 0x02, 0x00 }); // slider 0, value 512
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
        await client.ConnectAsync(new Uri($"ws://localhost:{_port}/"), CancellationToken.None);

        var frame1 = FramePayload(new byte[] { 0x01, 0x01, 0x01, 0x00 }); // slider 1, value 256
        var frame2 = FramePayload(new byte[] { 0x02, 0x02, 0x01 });       // button 2, pressed
        await client.SendAsync(frame1, WebSocketMessageType.Binary, true, CancellationToken.None);
        await client.SendAsync(frame2, WebSocketMessageType.Binary, true, CancellationToken.None);

        await Task.Delay(100);

        Assert.Equal(2, _messages.Count);
        Assert.IsType<SliderMessage>(_messages[0]);
        Assert.IsType<ButtonMessage>(_messages[1]);
    }

    [Fact]
    public void Connect_WithInvalidPort_SetsStatusToError()
    {
        // Port 80 typically requires elevated privileges and will fail
        var connection = new WebSocketConnection(new WebSocketConfig { Port = 80 });
        var statuses = new List<ConnectionStatus>();
        connection.StatusChanged += (_, s) => statuses.Add(s);

        connection.Connect();

        Assert.Equal(ConnectionStatus.Error, connection.Status);
        connection.Dispose();
    }
}
